using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using CouponOps.Domain;
using CouponOps.Infrastructure.Redis;
using Microsoft.Extensions.Options;

namespace CouponOps.Application;

public sealed record SpinPrize(
    long PrizeId, int SlotIndex, string Name, long ItemId, int ItemQty, bool IsJackpot, bool IsBlank);

public sealed record SpinResult(
    DrawResult Result,
    SpinPrize? Prize,
    bool FallbackApplied,
    bool PityApplied,
    int PityCount,
    int RemainingTickets,
    int RemainingDraws,
    DrawResult? PriorResult,
    Guid RequestId,
    int LatencyMs);

/// <summary>
/// 추첨 유스케이스. 판정은 전부 Redis 스크립트 안에서 일어나고,
/// 여기서는 난수 생성·날짜 경계 계산·샘플링 결정·이름 해소만 한다.
/// </summary>
public sealed class SpinDrawService(
    IDrawStore store,
    DrawMetaCache metaCache,
    IOptions<DrawOptions> options,
    TimeProvider clock)
{
    private readonly DrawOptions _opts = options.Value;

    /// <param name="clientIp">
    /// 이상 탐지용으로 이력에 남긴다. 추첨 판정에는 전혀 쓰이지 않는다 —
    /// 판정이 IP 에 의존하면 위조 한 번으로 결과가 달라진다.
    /// </param>
    public async Task<SpinResult> SpinAsync(
        long drawEventId, string userId, Guid? requestId, string? clientIp, CancellationToken ct)
    {
        // RequestId 를 클라이언트가 주지 않으면 서버가 만든다. 이 경우 재시도 멱등성은
        // 보장되지 않는다(매번 새 키). 확률 이벤트에서 이것은 특히 중요하다 —
        // 멱등 키 없는 재시도는 사실상 재추첨이다.
        var rid = requestId ?? Guid.NewGuid();
        var now = clock.GetUtcNow().UtcDateTime;

        var meta = await metaCache.GetAsync(drawEventId, ct);
        if (meta is null)
            return new SpinResult(DrawResult.SystemError, null, false, false, -1, -1, -1, null, rid, 0);

        var logFailure = _opts.FailureLogSampleRate >= 1.0
            || Random.Shared.NextDouble() < _opts.FailureLogSampleRate;

        var started = Stopwatch.GetTimestamp();
        var outcome = await store.SpinAsync(
            drawEventId, userId, rid, now,
            NextRandomValue(), DrawDay.For(now, meta.DailyResetAt), logFailure, clientIp, ct);
        var latency = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        SpinPrize? prize = null;
        if (outcome.PrizeId > 0 && meta.Prizes.TryGetValue(outcome.PrizeId, out var pm))
            prize = new SpinPrize(
                pm.PrizeId, pm.SlotIndex, pm.Name, outcome.ItemId, outcome.ItemQty, pm.IsJackpot, pm.IsBlank);

        return new SpinResult(
            outcome.Result, prize, outcome.FallbackApplied, outcome.PityApplied, outcome.PityCountAfter,
            outcome.RemainingTickets, outcome.RemainingDraws, outcome.PriorResult, rid, latency);
    }

    /// <summary>
    /// 추첨용 난수.
    /// </summary>
    /// <remarks>
    /// <see cref="RandomNumberGenerator"/> 를 쓰는 이유는 예측 불가능성이다 —
    /// 의사난수의 시드를 알아낼 수 있으면 잭팟이 나올 시점을 맞출 수 있다.
    /// <para>
    /// 48비트로 자르는 것은 Lua 때문이다. Lua 5.1 의 수는 double 이라 2^53 을 넘으면
    /// 정수 정밀도를 잃는다. 가중치 합이 int 범위(2^31)이므로 48비트면 나머지 연산의
    /// 편향은 2^-17 수준으로 무시할 수 있다.
    /// </para>
    /// </remarks>
    public static long NextRandomValue()
    {
        Span<byte> buffer = stackalloc byte[8];
        RandomNumberGenerator.Fill(buffer);
        return (long)(BinaryPrimitives.ReadUInt64LittleEndian(buffer) & 0x0000_FFFF_FFFF_FFFFUL);
    }
}

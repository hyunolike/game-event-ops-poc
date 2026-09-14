using System.Diagnostics;
using CouponOps.Domain;
using CouponOps.Infrastructure.Redis;
using Microsoft.Extensions.Options;

namespace CouponOps.Application;

public sealed record IssueCouponResult(
    IssueResult Result, string? CouponCode, int Remaining, IssueResult? PriorResult,
    Guid RequestId, int LatencyMs);

/// <summary>
/// 발급 유스케이스. 상태 판단은 전부 Redis 스크립트 안에서 일어나고,
/// 여기서는 요청 조립·샘플링 결정·지연 측정만 한다.
/// </summary>
public sealed class IssueCouponService(
    IIssuanceStore store,
    IOptions<IssuanceOptions> options,
    TimeProvider clock)
{
    private readonly IssuanceOptions _opts = options.Value;

    public async Task<IssueCouponResult> IssueAsync(
        long eventId, string userId, Guid? requestId, CancellationToken ct)
    {
        // RequestId 를 클라이언트가 주지 않으면 서버가 만든다. 이 경우 재시도 멱등성은
        // 보장되지 않는다(매번 새 키). 멱등이 필요한 클라이언트는 직접 생성해 보내야 한다.
        var rid = requestId ?? Guid.NewGuid();

        // 샘플링 결정은 앱에서 한다. Lua 안에서 난수를 쓰면 스크립트가 비결정적이 되고,
        // 복제·AOF 재생 시 마스터와 다른 판단을 하게 된다.
        var logFailure = _opts.FailureLogSampleRate >= 1.0
            || Random.Shared.NextDouble() < _opts.FailureLogSampleRate;

        var started = Stopwatch.GetTimestamp();
        var outcome = await store.IssueAsync(eventId, userId, rid, clock.GetUtcNow().UtcDateTime, logFailure, ct);
        var latency = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        return new IssueCouponResult(
            outcome.Result, outcome.CouponCode, outcome.Remaining, outcome.PriorResult, rid, latency);
    }
}

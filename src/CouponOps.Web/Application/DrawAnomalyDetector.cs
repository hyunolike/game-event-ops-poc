using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using CouponOps.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CouponOps.Application;

public enum AnomalyKind
{
    /// <summary>같은 유저의 잭팟 반복 당첨.</summary>
    JackpotRepeat = 0,
    /// <summary>짧은 시간 안의 추첨 폭주. 매크로·오토의 전형적인 모양이다.</summary>
    DrawBurst = 1,
}

public sealed record AnomalyRow(AnomalyKind Kind, string UserId, int Count, string Detail);

/// <summary>
/// 추첨 이력에서 봐야 할 패턴을 찾는다.
/// </summary>
/// <remarks>
/// <b>이것은 "부정 판정" 이 아니라 "봐야 할 것" 이다.</b>
/// 0.5% 짜리 잭팟을 세 번 받는 일은 확률적으로 불가능하지 않다 — 드물 뿐이다.
/// 그래서 자동으로 회수하거나 차단하지 않고, 운영자가 보게 띄우기만 한다.
/// 자동 제재는 오탐 한 건의 비용이 탐지 이득보다 크다.
/// <para>
/// 질의는 이미 깔아 둔 인덱스를 그대로 탄다 —
/// 잭팟 반복은 <c>(DrawEventId, PrizeId, RequestedAt)</c> 필터드(Result=1),
/// 폭주는 <c>(DrawEventId, RequestedAt)</c>.
/// </para>
/// </remarks>
public sealed class DrawAnomalyDetector(
    AppDbContext db, IOptions<DrawOptions> options, TimeProvider clock)
{
    private readonly DrawOptions _opts = options.Value;

    /// <summary>한 화면에 띄울 최대 건수. 이보다 많으면 개별 유저가 아니라 설정을 의심해야 한다.</summary>
    private const int MaxRows = 50;

    public async Task<IReadOnlyList<AnomalyRow>> ScanAsync(long drawEventId, CancellationToken ct)
    {
        var rows = new List<AnomalyRow>();

        rows.AddRange(await ScanJackpotRepeatsAsync(drawEventId, ct));
        rows.AddRange(await ScanBurstsAsync(drawEventId, ct));

        return rows;
    }

    private async Task<IReadOnlyList<AnomalyRow>> ScanJackpotRepeatsAsync(
        long drawEventId, CancellationToken ct)
    {
        var jackpotIds = await db.DrawPrizes.AsNoTracking()
            .Where(p => p.DrawEventId == drawEventId && p.IsJackpot)
            .Select(p => p.Id)
            .ToListAsync(ct);

        if (jackpotIds.Count == 0) return [];

        var threshold = _opts.JackpotRepeatThreshold;

        var repeats = await db.DrawLogs.AsNoTracking()
            .Where(l => l.DrawEventId == drawEventId
                        && l.Result == DrawResult.Won
                        && l.PrizeId != null
                        && jackpotIds.Contains(l.PrizeId.Value))
            .GroupBy(l => l.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .Where(x => x.Count >= threshold)
            .OrderByDescending(x => x.Count)
            .Take(MaxRows)
            .ToListAsync(ct);

        return repeats.Select(r => new AnomalyRow(
            AnomalyKind.JackpotRepeat, r.UserId, r.Count,
            $"잭팟 {r.Count}회 당첨 (임계 {threshold}회)")).ToList();
    }

    private async Task<IReadOnlyList<AnomalyRow>> ScanBurstsAsync(long drawEventId, CancellationToken ct)
    {
        var since = clock.GetUtcNow().UtcDateTime - _opts.BurstWindow;
        var threshold = _opts.BurstThreshold;

        // 성공·실패를 가리지 않는다. 한도에 막혀 계속 두드리는 것도 같은 신호다.
        var bursts = await db.DrawLogs.AsNoTracking()
            .Where(l => l.DrawEventId == drawEventId && l.RequestedAt >= since)
            .GroupBy(l => l.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .Where(x => x.Count >= threshold)
            .OrderByDescending(x => x.Count)
            .Take(MaxRows)
            .ToListAsync(ct);

        var minutes = (int)_opts.BurstWindow.TotalMinutes;

        return bursts.Select(b => new AnomalyRow(
            AnomalyKind.DrawBurst, b.UserId, b.Count,
            $"최근 {minutes}분 동안 {b.Count:N0}회 시도 (임계 {threshold:N0}회)")).ToList();
    }
}

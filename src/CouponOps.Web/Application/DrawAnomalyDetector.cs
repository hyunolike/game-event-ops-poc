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
    /// <summary>같은 IP 에서 여러 계정이 잭팟을 받았다. 다계정의 전형적인 모양이다.</summary>
    MultiAccountIp = 2,
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
    AppDbContext db,
    IOptions<DrawOptions> options,
    IOptions<NetworkOptions> network,
    TimeProvider clock)
{
    private readonly DrawOptions _opts = options.Value;
    private readonly NetworkOptions _network = network.Value;

    /// <summary>
    /// IP 축을 돌릴 수 있는 배포인가. 프록시 뒤에서 신뢰 설정이 없으면 모든 요청이 프록시 IP
    /// 하나로 보이고, 그 상태의 IP 축은 전원을 이상 징후로 띄우는 오탐 장치다.
    /// 화면이 "왜 안 나오는지" 를 말할 수 있도록 공개한다.
    /// </summary>
    public bool ClientIpUsable => _network.EffectiveClientIpTrusted;

    /// <summary>한 화면에 띄울 최대 건수. 이보다 많으면 개별 유저가 아니라 설정을 의심해야 한다.</summary>
    private const int MaxRows = 50;

    public async Task<IReadOnlyList<AnomalyRow>> ScanAsync(long drawEventId, CancellationToken ct)
    {
        var rows = new List<AnomalyRow>();

        rows.AddRange(await ScanJackpotRepeatsAsync(drawEventId, ct));
        rows.AddRange(await ScanBurstsAsync(drawEventId, ct));

        if (ClientIpUsable) rows.AddRange(await ScanMultiAccountIpsAsync(drawEventId, ct));

        return rows;
    }

    /// <summary>
    /// 같은 IP 에서 잭팟을 받은 서로 다른 계정을 찾는다.
    /// </summary>
    /// <remarks>
    /// 전체 추첨이 아니라 <b>잭팟 당첨 건만</b> 본다. 이유가 둘이다 —
    /// 집합이 재고 수만큼으로 작아 질의가 싸고(필터드 인덱스가 UserId·ClientIp 를 INCLUDE 한다),
    /// 다계정으로 노리는 것이 애초에 잭팟이기 때문이다. 골드 몇 번 더 받는 다계정은 볼 값이 없다.
    /// <para>
    /// 가족·PC방·회사가 IP 를 공유하면 같은 모양이 나온다. 그래서 판정이 아니라 확인 대상이다.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<AnomalyRow>> ScanMultiAccountIpsAsync(
        long drawEventId, CancellationToken ct)
    {
        var jackpotIds = await db.DrawPrizes.AsNoTracking()
            .Where(p => p.DrawEventId == drawEventId && p.IsJackpot)
            .Select(p => p.Id)
            .ToListAsync(ct);

        if (jackpotIds.Count == 0) return [];

        // (IP, 유저) 쌍을 DISTINCT 로 받아 앱에서 센다.
        // COUNT(DISTINCT) 를 LINQ 로 표현하는 방법은 EF 버전에 따라 번역이 갈리는데,
        // 번역 실패는 화면을 여는 순간 예외가 된다. 잭팟 당첨 집합은 작으므로 이쪽이 안전하고 싸다.
        var pairs = await db.DrawLogs.AsNoTracking()
            .Where(l => l.DrawEventId == drawEventId
                        && l.Result == DrawResult.Won
                        && l.PrizeId != null
                        && jackpotIds.Contains(l.PrizeId.Value)
                        && l.ClientIp != null)
            .Select(l => new { l.ClientIp, l.UserId })
            .Distinct()
            .ToListAsync(ct);

        var threshold = _opts.MultiAccountIpThreshold;

        return pairs
            .GroupBy(x => x.ClientIp!)
            .Where(g => g.Count() >= threshold)
            .OrderByDescending(g => g.Count())
            .Take(MaxRows)
            .Select(g => new AnomalyRow(
                AnomalyKind.MultiAccountIp, g.Key, g.Count(),
                $"같은 IP 에서 {g.Count()}개 계정이 잭팟 당첨 (임계 {threshold}개) — "
                + string.Join(", ", g.Select(x => x.UserId).Order().Take(5))))
            .ToList();
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

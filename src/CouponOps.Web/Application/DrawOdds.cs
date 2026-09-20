using CouponOps.Domain;

namespace CouponOps.Application;

/// <param name="Percent">표시용 확률(%). 반올림 잔차가 보정돼 합이 정확히 100.00 이 된다.</param>
public sealed record OddsRow(
    long PrizeId, int SlotIndex, string Name, long ItemId, int ItemQty,
    int Weight, int Stock, bool IsJackpot, bool IsBlank, decimal Percent);

/// <summary>
/// 가중치로부터 표시용 확률을 계산한다.
/// </summary>
/// <remarks>
/// 확률은 <b>저장하지 않고 표시할 때 계산</b>한다 — 이벤트 상태를 파생시키는 것과 같은 원칙이다.
/// 어드민의 실시간 미리보기와 공개 공시 페이지가 반드시 같은 함수를 써야 한다.
/// 둘이 다른 계산을 하는 순간, 언젠가 반드시 어긋난다.
/// </remarks>
public static class DrawOdds
{
    /// <summary>공시 표기 소수 자릿수.</summary>
    public const int Decimals = 2;

    /// <summary>
    /// 반올림하면 합이 99.99% 나 100.01% 가 된다.
    /// 잔차는 <b>가중치가 가장 큰 항목</b>에 흡수시킨다 — 가장 큰 항목이 0.01% 틀리는 것은
    /// 표시상 무해한 반면, 합계가 100.00% 가 아닌 공시는 그 자체로 문의를 만든다.
    /// </summary>
    public static IReadOnlyList<OddsRow> Compute(
        IReadOnlyList<DrawPrize> prizes, IReadOnlyDictionary<long, int>? liveStock = null)
    {
        var total = prizes.Sum(p => (long)p.Weight);
        if (total <= 0) return [];

        var rows = prizes
            .OrderBy(p => p.SlotIndex)
            .Select(p => new OddsRow(
                p.Id, p.SlotIndex, p.Name, p.ItemId, p.ItemQty, p.Weight,
                Stock: liveStock is not null && liveStock.TryGetValue(p.Id, out var s)
                    ? s
                    : p.InitialStock,
                p.IsJackpot, p.IsBlank,
                Percent: Math.Round(p.Weight * 100m / total, Decimals, MidpointRounding.AwayFromZero)))
            .ToList();

        var residual = 100m - rows.Sum(r => r.Percent);
        if (residual == 0m) return rows;

        var anchor = rows.MaxBy(r => r.Weight)!;
        rows[rows.IndexOf(anchor)] = anchor with { Percent = anchor.Percent + residual };
        return rows;
    }

    /// <summary>합계 행. 보정이 제대로 됐다면 항상 100.00 이다.</summary>
    public static decimal Sum(IReadOnlyList<OddsRow> rows) => rows.Sum(r => r.Percent);
}

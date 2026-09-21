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

    /// <summary>
    /// 한정 경품 소진 시의 공시 문구.
    /// </summary>
    /// <remarks>
    /// 확률 계산과 같은 이유로 여기 한 곳에만 둔다 — 공개 공시 페이지와 공시 API 가
    /// 각자 문구를 만들면, 둘이 어긋나는 순간 어느 쪽이 맞는지 아무도 말할 수 없다.
    /// <para>
    /// 대체(Fallback) 정책이므로 "소진되면 확률이 바뀐다" 가 아니라 "무엇으로 대체되는가" 를 밝힌다.
    /// 표시된 확률은 재고와 무관하게 언제나 실행 확률과 같다 — 그것이 이 정책을 택한 이유다.
    /// </para>
    /// </remarks>
    public static string SoldOutNotice(IReadOnlyList<DrawPrize> prizes)
    {
        var limited = prizes.Where(p => !p.IsUnlimited).ToList();
        if (limited.Count == 0) return "";

        // 슬롯은 많아야 열몇 개다. 사전을 만들면 Id 가 아직 없는(저장 전) 경품에서
        // 중복 키로 터지는데, 그 위험을 감수할 만큼 빠를 이유가 없다.
        var replacements = limited
            .Select(p => p.FallbackPrizeId)
            .Where(id => id is not null)
            .Select(id => prizes.FirstOrDefault(f => f.Id == id!.Value)?.Name)
            .Where(n => n is not null)
            .Distinct()
            .ToList();

        return replacements.Count == 0
            ? "한정 수량 경품은 소진 시 지급되지 않습니다."
            : $"한정 수량 경품 소진 시 해당 확률은 '{string.Join(", ", replacements)}' 지급으로 대체됩니다. "
              + "표시된 확률은 소진 여부와 무관하게 변하지 않습니다.";
    }
}

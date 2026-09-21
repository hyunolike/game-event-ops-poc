using CouponOps.Domain;

namespace CouponOps.Application;

/// <param name="ExpectedDraws">기대 당첨 횟수.</param>
/// <param name="ExpectedGrants">실제 지급 예상 개수(재고 상한 반영).</param>
/// <param name="OverflowDraws">재고를 넘겨 대체 경품으로 치환될 것으로 보이는 횟수.</param>
public sealed record SimulationRow(
    long PrizeId, int SlotIndex, string Name, long ItemId, int ItemQty,
    decimal Percent, decimal ExpectedDraws, long ExpectedGrants, long OverflowDraws,
    int Stock, bool IsJackpot, bool IsBlank);

public sealed record SimulationReport(
    int Draws, int TotalWeight, IReadOnlyList<SimulationRow> Rows, IReadOnlyList<string> Warnings);

/// <summary>
/// 저장 전 확률 시뮬레이션.
/// </summary>
/// <remarks>
/// <b>가중치 5 를 50 으로 잘못 친 사고는 이 화면에서만 걸린다.</b>
/// 그래서 정보성 화면이 아니라 저장 전에 반드시 통과해야 하는 게이트로 둔다.
/// <para>
/// 몬테카를로를 돌리지 않고 기댓값을 바로 계산하는 이유는 두 가지다 —
/// 결과가 실행할 때마다 달라지면 운영자가 "내가 본 숫자" 를 근거로 말할 수 없고,
/// 검증하려는 것이 분산이 아니라 규모(재고·지급량)이기 때문이다.
/// 분포가 가중치와 맞는지는 별도의 카이제곱 테스트가 본다.
/// </para>
/// </remarks>
public static class DrawSimulator
{
    /// <summary>기대 지급량이 재고의 이 배수를 넘으면 경고한다.</summary>
    private const decimal OverflowWarnRatio = 1.5m;

    public static SimulationReport Run(IReadOnlyList<DrawPrize> prizes, int draws)
    {
        var total = prizes.Sum(p => p.Weight);
        if (total <= 0 || draws <= 0)
            return new SimulationReport(draws, total, [], ["가중치 합과 시뮬레이션 횟수가 모두 1 이상이어야 합니다."]);

        var odds = DrawOdds.Compute(prizes);
        var oddsBySlot = odds.ToDictionary(o => o.SlotIndex);

        var rows = new List<SimulationRow>();
        var warnings = new List<string>();

        foreach (var p in prizes.OrderBy(p => p.SlotIndex))
        {
            var expected = draws * (decimal)p.Weight / total;

            long grants, overflow;
            if (p.IsUnlimited || p.IsBlank)
            {
                grants = p.IsBlank ? 0 : (long)Math.Round(expected * p.ItemQty, MidpointRounding.AwayFromZero);
                overflow = 0;
            }
            else
            {
                var capped = Math.Min(expected, p.InitialStock);
                grants = (long)Math.Round(capped * p.ItemQty, MidpointRounding.AwayFromZero);
                overflow = (long)Math.Round(Math.Max(0, expected - p.InitialStock), MidpointRounding.AwayFromZero);

                if (p.InitialStock > 0 && expected > p.InitialStock * OverflowWarnRatio)
                    warnings.Add(
                        $"'{p.Name}' 은 {draws:N0}회 기준 기대 당첨 {expected:N1}회로 재고 {p.InitialStock:N0}개를 "
                        + $"크게 넘습니다. {overflow:N0}회가 대체 경품으로 치환됩니다 — 가중치나 재고를 재검토하세요.");
            }

            rows.Add(new SimulationRow(
                p.Id, p.SlotIndex, p.Name, p.ItemId, p.ItemQty,
                oddsBySlot.TryGetValue(p.SlotIndex, out var o) ? o.Percent : 0m,
                Math.Round(expected, 1), grants, overflow,
                p.InitialStock, p.IsJackpot, p.IsBlank));
        }

        if (prizes.Any(p => p.IsJackpot))
            warnings.Add("잭팟 경품이 포함돼 있습니다. 활성화에는 다른 편집자의 승인이 필요합니다.");

        return new SimulationReport(draws, total, rows, warnings);
    }
}

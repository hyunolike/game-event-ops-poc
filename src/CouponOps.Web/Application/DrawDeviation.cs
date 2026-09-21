using CouponOps.Domain;

namespace CouponOps.Application;

/// <param name="Deviation">기대 대비 편차 비율(%). 기대값이 0이면 null.</param>
public sealed record DeviationRow(
    long PrizeId, int SlotIndex, string Name, decimal Percent,
    long Observed, decimal Expected, decimal? Deviation, int Stock, bool IsJackpot);

/// <param name="ChiSquare">관측 분포와 가중치의 적합도. 표본이 부족하면 null.</param>
/// <param name="Critical">유의수준 0.001 의 임계값. <see cref="ChiSquare"/> 가 이를 넘으면 경고한다.</param>
public sealed record DeviationReport(
    long TotalDraws, IReadOnlyList<DeviationRow> Rows,
    double? ChiSquare, double Critical, bool Alarming);

/// <summary>
/// 실제 당첨 분포가 설정한 가중치와 맞는지 본다.
/// </summary>
/// <remarks>
/// 편차가 지속적으로 크다는 건 셋 중 하나다 — 가중치 설정 오류, 워밍업 데이터 불일치, 어뷰징.
/// 셋 다 즉시 알아야 하는 사건이라 운영툴 상세 화면에 상시 띄운다.
/// </remarks>
public static class DrawDeviation
{
    /// <summary>카이제곱 검정에 필요한 최소 표본. 이보다 적으면 편차가 의미를 갖지 못한다.</summary>
    public const int MinimumSample = 200;

    public static DeviationReport Compute(
        IReadOnlyList<DrawPrize> prizes,
        IReadOnlyDictionary<long, long> winCounts,
        IReadOnlyDictionary<long, int> liveStock)
    {
        var odds = DrawOdds.Compute(prizes, liveStock);
        var total = winCounts.Values.Sum();

        var rows = new List<DeviationRow>();
        double? chiSquare = total >= MinimumSample ? 0.0 : null;

        foreach (var o in odds)
        {
            var observed = winCounts.GetValueOrDefault(o.PrizeId);
            var expected = total * o.Percent / 100m;

            rows.Add(new DeviationRow(
                o.PrizeId, o.SlotIndex, o.Name, o.Percent, observed, Math.Round(expected, 1),
                Deviation: expected == 0 ? null : Math.Round((observed - expected) * 100m / expected, 1),
                o.Stock, o.IsJackpot));

            if (chiSquare is not null && expected > 0)
            {
                var diff = observed - (double)expected;
                chiSquare += diff * diff / (double)expected;
            }
        }

        // 자유도 = 슬롯 수 - 1, 유의수준 0.001.
        var critical = CriticalValue(Math.Max(1, prizes.Count - 1));

        return new DeviationReport(
            total, rows, chiSquare, critical,
            Alarming: chiSquare is { } chi && chi > critical);
    }

    /// <summary>
    /// 자유도별 카이제곱 임계값(α = 0.001).
    /// </summary>
    /// <remarks>
    /// 분포 함수를 직접 구현하지 않고 표를 쓴다 — 운영 화면의 경고선에 필요한 정밀도는
    /// 소수 셋째 자리가 아니라 "넘었나 아닌가" 뿐이고, 표는 검증할 수 있지만 구현은 그렇지 않다.
    /// 표를 벗어나는 자유도(슬롯 20개 초과)는 Wilson–Hilferty 근사로 잇는다.
    /// </remarks>
    private static double CriticalValue(int degreesOfFreedom)
    {
        double[] table =
        [
            0,      10.828, 13.816, 16.266, 18.467, 20.515, 22.458, 24.322, 26.124, 27.877,
            29.588, 31.264, 32.909, 34.528, 36.123, 37.697, 39.252, 40.790, 42.312, 43.820,
            45.315,
        ];

        if (degreesOfFreedom < table.Length) return table[degreesOfFreedom];

        // Wilson–Hilferty: χ²(k, α) ≈ k·(1 − 2/(9k) + z·√(2/(9k)))³,  z(0.999) ≈ 3.0902
        const double z = 3.0902;
        var k = (double)degreesOfFreedom;
        var t = 2.0 / (9.0 * k);
        return k * Math.Pow(1 - t + z * Math.Sqrt(t), 3);
    }
}

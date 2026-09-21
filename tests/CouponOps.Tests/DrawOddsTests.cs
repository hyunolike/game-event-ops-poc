using CouponOps.Application;
using CouponOps.Domain;
using CouponOps.Tests.Infrastructure;
using FluentAssertions;

namespace CouponOps.Tests;

/// <summary>
/// 확률 표기와 시뮬레이터. 컨테이너가 필요 없는 순수 계산이다.
/// </summary>
public sealed class DrawOddsTests
{
    [Fact(DisplayName = "표시 확률의 합은 항상 정확히 100.00% 다")]
    public void Displayed_percentages_always_sum_to_exactly_100()
    {
        // 3등분은 반올림으로 합이 99.99% 가 되는 대표적인 경우다.
        List<DrawPrize> thirds =
        [
            DrawPrize.Create(0, "A", 1, 1, weight: 1, initialStock: -1, isJackpot: false, isBlank: false),
            DrawPrize.Create(1, "B", 2, 1, weight: 1, initialStock: -1, isJackpot: false, isBlank: false),
            DrawPrize.Create(2, "C", 3, 1, weight: 1, initialStock: -1, isJackpot: false, isBlank: false),
        ];

        var rows = DrawOdds.Compute(thirds);

        DrawOdds.Sum(rows).Should().Be(100.00m,
            "합계가 99.99% 로 보이는 공시는 그 자체로 문의를 만든다");
    }

    [Fact(DisplayName = "반올림 잔차는 가중치가 가장 큰 항목이 흡수한다")]
    public void Rounding_residual_lands_on_the_largest_weight()
    {
        List<DrawPrize> prizes =
        [
            DrawPrize.Create(0, "희귀", 1, 1, weight: 1, initialStock: -1, isJackpot: false, isBlank: false),
            DrawPrize.Create(1, "흔함", 2, 1, weight: 2, initialStock: -1, isJackpot: false, isBlank: false),
        ];

        var rows = DrawOdds.Compute(prizes);

        // 1/3 = 33.33%, 2/3 = 66.67% → 합 100.00%. 잔차는 큰 쪽이 가져간다.
        rows.Single(r => r.Name == "희귀").Percent.Should().Be(33.33m);
        rows.Single(r => r.Name == "흔함").Percent.Should().Be(66.67m);
    }

    [Fact(DisplayName = "표준 구성의 확률이 설계 문서와 일치한다")]
    public void Standard_layout_matches_the_documented_odds()
    {
        var rows = DrawOdds.Compute(DrawTestData.StandardPrizes());

        rows.Single(r => r.SlotIndex == 0).Percent.Should().Be(0.50m);
        rows.Single(r => r.SlotIndex == 1).Percent.Should().Be(4.50m);
        rows.Single(r => r.SlotIndex == 2).Percent.Should().Be(20.00m);
        rows.Single(r => r.SlotIndex == 3).Percent.Should().Be(75.00m);
        DrawOdds.Sum(rows).Should().Be(100.00m);
    }

    [Fact(DisplayName = "시뮬레이터는 기대 지급이 재고를 크게 넘으면 경고한다")]
    public void Simulator_warns_when_expected_grants_exceed_stock()
    {
        // 잭팟 재고 10개에 10만 회 → 기대 당첨 500회. 재고의 50배다.
        var report = DrawSimulator.Run(DrawTestData.StandardPrizes(jackpotStock: 10), draws: 100_000);

        report.Warnings.Should().Contain(w => w.Contains("전설 무기 상자"));

        var jackpot = report.Rows.Single(r => r.SlotIndex == 0);
        jackpot.ExpectedDraws.Should().Be(500.0m);
        jackpot.ExpectedGrants.Should().Be(10, "재고를 넘겨 지급될 수는 없다");
        jackpot.OverflowDraws.Should().Be(490, "나머지는 대체 경품으로 치환된다");
    }

    [Fact(DisplayName = "시뮬레이터는 가중치 오타를 규모로 드러낸다")]
    public void Simulator_surfaces_a_weight_typo_as_scale()
    {
        // 가중치 5 를 50 으로 잘못 친 상황. 확률이 0.5% → 4.76% 로 10배 가까이 뛴다.
        var correct = DrawSimulator.Run(DrawTestData.StandardPrizes(), draws: 10_000);
        var typo = DrawTestData.StandardPrizes();
        typo[0] = DrawPrize.Create(0, "전설 무기 상자", 9001, 1,
            weight: 50, initialStock: 10, isJackpot: true, isBlank: false, fallbackSlotIndex: 3);
        var wrong = DrawSimulator.Run(typo, draws: 10_000);

        correct.Rows[0].ExpectedDraws.Should().Be(50.0m);
        wrong.Rows[0].ExpectedDraws.Should().BeGreaterThan(400m,
            "저장 전에 이 숫자를 보면 오타를 알아챌 수 있다 — 그래서 게이트로 둔다");
    }

    [Fact(DisplayName = "무제한 경품만 있으면 소진 문구가 없다")]
    public void No_sold_out_notice_when_everything_is_unlimited()
    {
        List<DrawPrize> prizes =
        [
            DrawPrize.Create(0, "강화 주문서", 3001, 5, weight: 1, initialStock: -1, isJackpot: false, isBlank: false),
            DrawPrize.Create(1, "골드", 1001, 1000, weight: 1, initialStock: -1, isJackpot: false, isBlank: false),
        ];

        DrawOdds.SoldOutNotice(prizes).Should().BeEmpty();
    }

    [Fact(DisplayName = "대체 경품이 없는 한정 경품은 '지급되지 않습니다' 로 밝힌다")]
    public void Limited_prizes_without_a_fallback_say_so()
    {
        List<DrawPrize> prizes =
        [
            DrawPrize.Create(0, "한정 상자", 9001, 1, weight: 1, initialStock: 10, isJackpot: true, isBlank: false),
            DrawPrize.Create(1, "골드", 1001, 1000, weight: 1, initialStock: -1, isJackpot: false, isBlank: false),
        ];

        DrawOdds.SoldOutNotice(prizes).Should().Contain("지급되지 않습니다");
    }

    [Fact(DisplayName = "잭팟이 있으면 2인 승인 경고가 붙는다")]
    public void Jackpot_requires_second_approval()
    {
        var report = DrawSimulator.Run(DrawTestData.StandardPrizes(), draws: 1_000);

        report.Warnings.Should().Contain(w => w.Contains("승인"));
    }
}

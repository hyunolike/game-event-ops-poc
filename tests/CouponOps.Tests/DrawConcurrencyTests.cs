using CouponOps.Domain;
using CouponOps.Tests.Infrastructure;
using FluentAssertions;

namespace CouponOps.Tests;

/// <summary>
/// 이 프로젝트가 증명하려는 성질의 룰렛 버전 —
/// <b>동시 요청이 아무리 몰려도 경품 재고를 넘긴 지급은 한 건도 없다.</b>
/// </summary>
[Collection(CouponOpsCollection.Name)]
public sealed class DrawConcurrencyTests(CouponOpsFixture fx)
{
    private DrawClient Client => new(fx.App);

    [Fact(DisplayName = "한정 경품은 동시 요청이 몰려도 재고를 넘겨 지급되지 않는다")]
    public async Task Limited_prize_never_exceeds_its_stock()
    {
        const int stock = 50;
        const int users = 400;

        // 슬롯 0 의 가중치가 999,999 대 1 이라 재고가 남아 있는 동안은 사실상 전부 슬롯 0 이 뽑힌다.
        // 즉 400건의 요청이 전부 같은 50개 재고를 향해 경쟁한다.
        var ev = await fx.CreateActiveDrawAsync(
            prizes: DrawTestData.ScarcePrizes(stock), ticketItemId: null, ticketCost: 0, dailyDrawLimit: 1);

        var requests = TestData.DistinctUsers(users, "rush");
        var calls = await Client.SpinConcurrentlyAsync(ev.Id, requests);

        calls.Should().OnlyContain(c => c.Result == nameof(DrawResult.Won),
            "추첨 자체는 전부 성공해야 한다 — 재고 소진은 대체 경품으로 흡수된다");

        var limitedWins = calls.Count(c => c.Body!.Prize!.SlotIndex == 0);
        limitedWins.Should().Be(stock, "재고를 넘긴 지급도, 모자란 지급도 없어야 한다");

        // 소진 이후의 요청은 전부 대체 경품(슬롯 1)으로 흘러야 한다.
        // "대체로 치환된 건수" 를 그대로 단언하지 않는 이유는, 아주 낮은 확률로
        // 슬롯 1 이 직접 뽑히는 경우가 섞이기 때문이다 — 그것까지 합쳐야 항등식이 된다.
        calls.Count(c => c.Body!.Prize!.SlotIndex == 1).Should().Be(users - stock);
        calls.Count(c => c.Body!.FallbackApplied).Should().BeGreaterThan(0,
            "재고 소진 뒤의 요청은 대체 치환을 거쳐야 한다");
        calls.Where(c => c.Body!.Prize!.SlotIndex == 0).Should()
            .OnlyContain(c => !c.Body!.FallbackApplied, "한정 경품 당첨에 대체가 끼어들 수 없다");

        var remaining = await fx.StockAsync(ev.Id);
        remaining.Values.Should().OnlyContain(v => v == 0, "재고 카운터가 음수로 내려가서는 안 된다");
    }

    [Fact(DisplayName = "티켓 1개로 동시에 여러 번 돌려도 한 번만 성공한다")]
    public async Task One_ticket_yields_exactly_one_spin()
    {
        var ev = await fx.CreateActiveDrawAsync(ticketCost: 1);
        await fx.GrantTicketsAsync(ev.Id, "single", 1);

        // 같은 유저가 서로 다른 RequestId 로 10번 동시에 누른 상황(더블클릭·재시도 폭주).
        var requests = Enumerable.Range(0, 10)
            .Select(_ => ("single", (Guid?)Guid.NewGuid())).ToArray();

        var calls = await Client.SpinConcurrentlyAsync(ev.Id, requests);

        calls.Count(c => c.Result == nameof(DrawResult.Won)).Should().Be(1);
        calls.Count(c => c.Result == nameof(DrawResult.InsufficientTicket)).Should().Be(9);
        (await fx.TicketsAsync(ev.Id, "single")).Should().Be(0, "티켓이 음수가 되면 안 된다");
    }

    [Fact(DisplayName = "성공한 추첨은 티켓을 정확히 한 번 차감한다")]
    public async Task Successful_spin_spends_exactly_one_ticket()
    {
        // 원칙 3의 반대편이다. 실패는 티켓을 건드리지 않아야 하고(DrawRuleTests),
        // 성공은 반드시 정확히 한 번 차감해야 한다.
        //
        // 참고: AllPrizesSoldOut 은 정상 설정에서는 도달할 수 없다 —
        // 도메인 검증이 "유한 재고 슬롯의 대체 대상은 무제한 재고" 를 강제하므로
        // 치환은 항상 한 번에 끝난다. 스크립트의 해당 분기는 데이터 손상에 대한 방어선이다.
        var ev = await fx.CreateActiveDrawAsync(ticketCost: 1);
        await fx.GrantTicketsAsync(ev.Id, "spender", 3);

        (await Client.SpinAsync(ev.Id, "spender")).Result.Should().Be(nameof(DrawResult.Won));
        (await fx.TicketsAsync(ev.Id, "spender")).Should().Be(2);

        (await Client.SpinAsync(ev.Id, "spender")).Body!.RemainingTickets.Should().Be(1);
        (await fx.TicketsAsync(ev.Id, "spender")).Should().Be(1);
    }
}

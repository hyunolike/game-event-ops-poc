using System.Net;
using CouponOps.Domain;
using CouponOps.Tests.Infrastructure;
using FluentAssertions;

namespace CouponOps.Tests;

/// <summary>추첨 거부 사유가 요구사항대로 구분되는지 확인한다.</summary>
[Collection(CouponOpsCollection.Name)]
public sealed class DrawRuleTests(CouponOpsFixture fx)
{
    private DrawClient Client => new(fx.App);

    [Fact(DisplayName = "시작 전 룰렛은 기간 외로 거부된다")]
    public async Task Before_start_is_rejected()
    {
        var now = DateTime.UtcNow;
        var ev = await fx.CreateActiveDrawAsync(
            startsAtUtc: now.AddHours(1), endsAtUtc: now.AddHours(2));
        await fx.GrantTicketsAsync(ev.Id, "u1", 10);

        var call = await Client.SpinAsync(ev.Id, "u1");

        call.Result.Should().Be(nameof(DrawResult.OutOfPeriod));
        call.Status.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact(DisplayName = "종료된 룰렛은 기간 외로 거부된다")]
    public async Task After_end_is_rejected()
    {
        var now = DateTime.UtcNow;
        var ev = await fx.CreateActiveDrawAsync(
            startsAtUtc: now.AddHours(-2), endsAtUtc: now.AddHours(-1));
        await fx.GrantTicketsAsync(ev.Id, "u1", 10);

        (await Client.SpinAsync(ev.Id, "u1")).Result.Should().Be(nameof(DrawResult.OutOfPeriod));
    }

    [Fact(DisplayName = "운영자가 중단하면 즉시 추첨이 막힌다")]
    public async Task Suspended_draw_stops_immediately()
    {
        var ev = await fx.CreateActiveDrawAsync();
        await fx.GrantTicketsAsync(ev.Id, "u1", 10);

        (await Client.SpinAsync(ev.Id, "u1")).Result.Should().Be(nameof(DrawResult.Won));

        await fx.SuspendDrawAsync(ev.Id, "확률 설정 오류 의심");

        var call = await Client.SpinAsync(ev.Id, "u1");
        call.Result.Should().Be(nameof(DrawResult.Suspended));
        call.Status.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact(DisplayName = "티켓이 없으면 거부되고 티켓은 차감되지 않는다")]
    public async Task No_ticket_is_rejected_without_spending()
    {
        var ev = await fx.CreateActiveDrawAsync(ticketCost: 2);
        await fx.GrantTicketsAsync(ev.Id, "poor", 1);   // 비용 2, 보유 1

        var call = await Client.SpinAsync(ev.Id, "poor");

        call.Result.Should().Be(nameof(DrawResult.InsufficientTicket));
        (await fx.TicketsAsync(ev.Id, "poor")).Should().Be(1,
            "거부된 추첨은 티켓을 건드리면 안 된다 — 판정이 전부 쓰기 이전에 끝나야 하는 이유다");
    }

    [Fact(DisplayName = "일일 횟수를 넘기면 거부된다")]
    public async Task Daily_limit_is_enforced()
    {
        var ev = await fx.CreateActiveDrawAsync(
            dailyDrawLimit: 3, ticketItemId: null, ticketCost: 0);

        for (var i = 0; i < 3; i++)
            (await Client.SpinAsync(ev.Id, "daily-user")).Result.Should().Be(nameof(DrawResult.Won));

        var call = await Client.SpinAsync(ev.Id, "daily-user");
        call.Result.Should().Be(nameof(DrawResult.DailyLimitExceeded));
        call.Status.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact(DisplayName = "일일 잔여 횟수가 응답에 정확히 실린다")]
    public async Task Remaining_draws_counts_down()
    {
        var ev = await fx.CreateActiveDrawAsync(
            dailyDrawLimit: 3, ticketItemId: null, ticketCost: 0);

        (await Client.SpinAsync(ev.Id, "counter")).Body!.RemainingDraws.Should().Be(2);
        (await Client.SpinAsync(ev.Id, "counter")).Body!.RemainingDraws.Should().Be(1);
        (await Client.SpinAsync(ev.Id, "counter")).Body!.RemainingDraws.Should().Be(0);
    }

    [Fact(DisplayName = "같은 RequestId 재시도는 같은 경품을 돌려주고 티켓을 두 번 깎지 않는다")]
    public async Task Same_request_id_replays_the_same_prize()
    {
        var ev = await fx.CreateActiveDrawAsync();
        await fx.GrantTicketsAsync(ev.Id, "retry", 10);
        var requestId = Guid.NewGuid();

        var first = await Client.SpinAsync(ev.Id, "retry", requestId);
        var ticketsAfterFirst = await fx.TicketsAsync(ev.Id, "retry");

        var second = await Client.SpinAsync(ev.Id, "retry", requestId);

        first.Result.Should().Be(nameof(DrawResult.Won));
        second.Result.Should().Be(nameof(DrawResult.DuplicateRequest));
        second.Body!.PriorResult.Should().Be(nameof(DrawResult.Won));
        second.Body.Prize!.PrizeId.Should().Be(first.Body!.Prize!.PrizeId,
            "재시도가 다른 결과를 주면 그것은 재추첨이다 — 네트워크 타임아웃마다 한 번 더 돌릴 수 있게 된다");
        second.Status.Should().Be(HttpStatusCode.OK);

        (await fx.TicketsAsync(ev.Id, "retry")).Should().Be(ticketsAfterFirst,
            "멱등 재생은 티켓을 소모하지 않는다");
    }

    [Fact(DisplayName = "천장에 도달하면 지정 경품이 확정 지급된다")]
    public async Task Pity_guarantees_the_designated_prize()
    {
        // 꽝 가중치가 999,999 대 1 이라 정상 추첨으로 당첨될 확률은 사실상 0 이다.
        // 그럼에도 10회차에 반드시 당첨돼야 한다 — 그것이 천장의 정의다.
        var ev = await fx.CreateActiveDrawAsync(
            prizes: DrawTestData.BlankHeavyPrizes(),
            ticketItemId: null, ticketCost: 0, dailyDrawLimit: 20,
            pityThreshold: 10, pitySlotIndex: 0);

        for (var i = 1; i <= 9; i++)
        {
            var call = await Client.SpinAsync(ev.Id, "pity-user");
            call.Body!.Prize!.IsBlank.Should().BeTrue($"{i}회차는 꽝이어야 한다");
            call.Body.PityApplied.Should().BeFalse();
            call.Body.PityCount.Should().Be(i);
        }

        var tenth = await Client.SpinAsync(ev.Id, "pity-user");
        tenth.Body!.PityApplied.Should().BeTrue();
        tenth.Body.Prize!.IsBlank.Should().BeFalse();
        tenth.Body.Prize.SlotIndex.Should().Be(0);
        tenth.Body.PityCount.Should().Be(0, "당첨되면 천장 카운터는 리셋된다");
    }

    [Fact(DisplayName = "워밍업되지 않은 이벤트는 시스템 오류로 구분된다")]
    public async Task Unknown_event_is_system_error()
    {
        var call = await Client.SpinAsync(999_999_999, "ghost");

        call.Result.Should().Be(nameof(DrawResult.SystemError));
        call.Status.Should().Be(HttpStatusCode.ServiceUnavailable);
    }
}

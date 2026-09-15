using System.Net;
using CouponOps.Domain;
using CouponOps.Tests.Infrastructure;
using FluentAssertions;

namespace CouponOps.Tests;

/// <summary>발급 거부 사유가 요구사항대로 구분되는지 확인한다.</summary>
[Collection(CouponOpsCollection.Name)]
public sealed class IssueRuleTests(CouponOpsFixture fx)
{
    private IssueClient Client => new(fx.App);

    [Fact(DisplayName = "시작 전 이벤트는 기간 외로 거부된다")]
    public async Task Before_start_is_rejected()
    {
        var now = DateTime.UtcNow;
        var ev = await fx.CreateEventAsync(now.AddHours(1), now.AddHours(2));

        var call = await Client.IssueAsync(ev.Id, "u1");

        call.Result.Should().Be(nameof(IssueResult.OutOfPeriod));
        call.Status.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact(DisplayName = "종료된 이벤트는 기간 외로 거부된다")]
    public async Task After_end_is_rejected()
    {
        var now = DateTime.UtcNow;
        var ev = await fx.CreateEventAsync(now.AddHours(-2), now.AddHours(-1));

        var call = await Client.IssueAsync(ev.Id, "u1");

        call.Result.Should().Be(nameof(IssueResult.OutOfPeriod));
    }

    [Fact(DisplayName = "운영자가 중단한 이벤트는 즉시 발급이 막힌다")]
    public async Task Suspended_event_stops_issuing_immediately()
    {
        var ev = await fx.CreateActiveEventAsync(totalQuantity: 10, perUserLimit: 1);

        (await Client.IssueAsync(ev.Id, "before-suspend")).Result
            .Should().Be(nameof(IssueResult.Success));

        await fx.SuspendAsync(ev.Id, "운영자 긴급 중단");

        var call = await Client.IssueAsync(ev.Id, "after-suspend");
        call.Result.Should().Be(nameof(IssueResult.Suspended));
        call.Status.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact(DisplayName = "동일 RequestId 재시도는 최초 발급 결과를 그대로 돌려준다")]
    public async Task Same_request_id_replays_the_original_outcome()
    {
        var ev = await fx.CreateActiveEventAsync(totalQuantity: 10, perUserLimit: 1);
        var requestId = Guid.NewGuid();

        var first = await Client.IssueAsync(ev.Id, "retry-user", requestId);
        var second = await Client.IssueAsync(ev.Id, "retry-user", requestId);

        first.Result.Should().Be(nameof(IssueResult.Success));

        second.Result.Should().Be(nameof(IssueResult.DuplicateRequest));
        second.Body!.PriorResult.Should().Be(nameof(IssueResult.Success));
        second.Body.CouponCode.Should().Be(first.Body!.CouponCode,
            "재시도는 같은 쿠폰을 돌려받아야 한다 — 두 장이 나가면 안 된다");

        // 재시도가 재고를 한 번 더 깎지 않았는지 확인한다.
        second.Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact(DisplayName = "재시도가 재고를 추가로 소모하지 않는다")]
    public async Task Retry_does_not_consume_extra_stock()
    {
        var ev = await fx.CreateActiveEventAsync(totalQuantity: 5, perUserLimit: 1);
        var requestId = Guid.NewGuid();

        // 같은 요청을 20번 재시도해도 소모는 1개여야 한다.
        for (var i = 0; i < 20; i++)
            await Client.IssueAsync(ev.Id, "flaky-client", requestId);

        // 남은 4개를 다른 유저들이 전부 가져갈 수 있어야 한다.
        var others = await Client.IssueConcurrentlyAsync(
            ev.Id, TestData.DistinctUsers(4, "other"));

        others.Should().OnlyContain(c => c.Result == nameof(IssueResult.Success));
        (await Client.IssueAsync(ev.Id, "late-comer")).Result
            .Should().Be(nameof(IssueResult.SoldOut));
    }

    [Fact(DisplayName = "워밍업되지 않은 이벤트는 소진이 아니라 시스템 오류로 구분된다")]
    public async Task Unwarmed_event_is_not_reported_as_sold_out()
    {
        var call = await Client.IssueAsync(eventId: 999_999, userId: "u1");

        call.Result.Should().Be(nameof(IssueResult.SystemError));
        call.Status.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact(DisplayName = "userId 가 없으면 400 으로 거부한다")]
    public async Task Missing_user_id_is_rejected()
    {
        var ev = await fx.CreateActiveEventAsync(totalQuantity: 5, perUserLimit: 1);
        var call = await Client.IssueAsync(ev.Id, "   ");

        call.Status.Should().Be(HttpStatusCode.BadRequest);
    }
}

using System.Net;
using CouponOps.Application;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using CouponOps.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CouponOps.Tests;

/// <summary>
/// 확률 변경의 2인 승인(maker-checker).
/// </summary>
/// <remarks>
/// 이 절차가 지키려는 것은 하나다 — <b>자기가 올린 확률 변경을 자기가 통과시킬 수 없다.</b>
/// 그게 안 되면 절차는 형식이고, 사고가 났을 때 "두 사람이 봤다" 고 말할 수 없다.
/// </remarks>
[Collection(CouponOpsCollection.Name)]
public sealed class DrawApprovalTests(CouponOpsFixture fx)
{
    private AdminClient NewClient() => new(fx.App);

    private static Dictionary<string, string> WeightForm(
        IReadOnlyList<int> weights, string reason) =>
        weights.Select((w, i) => (Key: $"Weights[{i}]", Value: w.ToString()))
            .Append((Key: "Reason", Value: reason))
            .Append((Key: "SimulationDraws", Value: "10000"))
            .ToDictionary(x => x.Key, x => x.Value);

    private async Task<(int VersionCount, int Slot0Weight, int Pending)> StateAsync(long drawEventId)
    {
        using var scope = fx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return (
            await db.DrawWeightVersions.CountAsync(v => v.DrawEventId == drawEventId),
            (await db.DrawPrizes.AsNoTracking()
                .SingleAsync(p => p.DrawEventId == drawEventId && p.SlotIndex == 0)).Weight,
            await db.DrawApprovals.CountAsync(a => a.DrawEventId == drawEventId
                                                && a.Status == ApprovalStatus.Pending));
    }

    private async Task<long> PendingIdAsync(long drawEventId)
    {
        using var scope = fx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.DrawApprovals.AsNoTracking()
            .Where(a => a.DrawEventId == drawEventId && a.Status == ApprovalStatus.Pending)
            .Select(a => a.Id)
            .SingleAsync();
    }

    [Fact(DisplayName = "시작 전 이벤트의 확률 변경은 승인 없이 바로 적용된다")]
    public async Task Scheduled_event_weights_apply_immediately()
    {
        var now = DateTime.UtcNow;
        var ev = await fx.CreateActiveDrawAsync(
            startsAtUtc: now.AddHours(2), endsAtUtc: now.AddHours(5));

        var client = NewClient();
        await client.LoginAsync("admin");

        var res = await client.PostFormAsync(
            $"/Draws/Weights?id={ev.Id}", $"/Draws/Weights?id={ev.Id}&handler=Request",
            WeightForm([50, 45, 200, 750], "오픈 전 밸런스 조정"));

        AdminClient.RedirectTarget(res).Should().Contain("/Draws/Details");

        var state = await StateAsync(ev.Id);
        state.Slot0Weight.Should().Be(50, "아직 아무도 뽑지 않았으므로 절차를 걸 이유가 없다");
        state.VersionCount.Should().Be(2, "새 확률표 버전이 활성화된다");
        state.Pending.Should().Be(0);
    }

    [Fact(DisplayName = "시작된 이벤트의 확률 변경은 대기에 들어가고 확률은 그대로다")]
    public async Task Started_event_weight_change_waits_for_approval()
    {
        var ev = await fx.CreateActiveDrawAsync();

        var client = NewClient();
        await client.LoginAsync("admin");

        var res = await client.PostFormAsync(
            $"/Draws/Weights?id={ev.Id}", $"/Draws/Weights?id={ev.Id}&handler=Request",
            WeightForm([50, 45, 200, 750], "1등 체감 개선"));

        AdminClient.RedirectTarget(res).Should().Contain("/Draws/Details");

        var state = await StateAsync(ev.Id);
        state.Pending.Should().Be(1);
        state.Slot0Weight.Should().Be(5, "승인 전에는 확률이 바뀌면 안 된다");
        state.VersionCount.Should().Be(1, "확률표 버전도 늘지 않는다");
    }

    [Fact(DisplayName = "요청자 본인은 자기 요청을 승인할 수 없다")]
    public async Task The_requester_cannot_approve_their_own_request()
    {
        var ev = await fx.CreateActiveDrawAsync();

        var client = NewClient();
        await client.LoginAsync("admin");
        await client.PostFormAsync(
            $"/Draws/Weights?id={ev.Id}", $"/Draws/Weights?id={ev.Id}&handler=Request",
            WeightForm([50, 45, 200, 750], "1등 체감 개선"));

        var approvalId = await PendingIdAsync(ev.Id);

        var res = await client.PostFormAsync(
            "/Draws/Approvals", "/Draws/Approvals?handler=Approve", new()
            {
                ["ApprovalId"] = approvalId.ToString(),
                ["Note"] = "제가 올린 건데 그냥 통과시킵니다",
            });

        res.StatusCode.Should().Be(HttpStatusCode.OK, "리다이렉트 없이 화면을 다시 렌더한다");
        (await res.Content.ReadAsStringAsync())
            .Should().Contain("본인이 올린 요청은 본인이 결정할 수 없습니다");

        (await StateAsync(ev.Id)).Slot0Weight.Should().Be(5, "확률은 그대로여야 한다");
    }

    [Fact(DisplayName = "다른 편집자가 승인하면 새 확률표가 활성화된다")]
    public async Task A_second_editor_can_approve_and_the_change_applies()
    {
        var ev = await fx.CreateActiveDrawAsync();

        var maker = NewClient();
        await maker.LoginAsync("admin");
        await maker.PostFormAsync(
            $"/Draws/Weights?id={ev.Id}", $"/Draws/Weights?id={ev.Id}&handler=Request",
            WeightForm([50, 45, 200, 750], "1등 체감 개선"));

        var approvalId = await PendingIdAsync(ev.Id);

        var checker = NewClient();
        await checker.LoginAsync("admin2");
        var res = await checker.PostFormAsync(
            "/Draws/Approvals", "/Draws/Approvals?handler=Approve", new()
            {
                ["ApprovalId"] = approvalId.ToString(),
                ["Note"] = "지급 규모 확인함",
            });

        AdminClient.RedirectTarget(res).Should().Contain("/Draws/Approvals");

        var state = await StateAsync(ev.Id);
        state.Slot0Weight.Should().Be(50);
        state.VersionCount.Should().Be(2);
        state.Pending.Should().Be(0);

        using var scope = fx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var approval = await db.DrawApprovals.AsNoTracking().SingleAsync(a => a.Id == approvalId);
        approval.Status.Should().Be(ApprovalStatus.Approved);
        approval.RequestedByLoginId.Should().Be("admin");
        approval.DecidedByLoginId.Should().Be("admin2", "누가 올리고 누가 통과시켰는지가 남아야 한다");

        // 과거 이력이 설명 가능한지 — 새 버전이 이전 버전을 대체하되 이전 버전은 남는다.
        var versions = await db.DrawWeightVersions.AsNoTracking()
            .Where(v => v.DrawEventId == ev.Id).OrderBy(v => v.Version).ToListAsync();
        versions[0].DeactivatedAt.Should().NotBeNull();
        versions[1].DeactivatedAt.Should().BeNull();
        versions[1].ChangeReason.Should().Contain("admin").And.Contain("admin2");
    }

    [Fact(DisplayName = "반려하면 확률은 그대로고 요청은 닫힌다")]
    public async Task Rejecting_leaves_the_odds_untouched()
    {
        var ev = await fx.CreateActiveDrawAsync();

        var maker = NewClient();
        await maker.LoginAsync("admin");
        await maker.PostFormAsync(
            $"/Draws/Weights?id={ev.Id}", $"/Draws/Weights?id={ev.Id}&handler=Request",
            WeightForm([500, 45, 200, 750], "1등 확률 100배"));

        var approvalId = await PendingIdAsync(ev.Id);

        var checker = NewClient();
        await checker.LoginAsync("admin2");
        await checker.PostFormAsync(
            "/Draws/Approvals", "/Draws/Approvals?handler=Reject", new()
            {
                ["ApprovalId"] = approvalId.ToString(),
                ["Note"] = "기대 지급 규모가 재고를 크게 넘습니다",
            });

        var state = await StateAsync(ev.Id);
        state.Slot0Weight.Should().Be(5);
        state.VersionCount.Should().Be(1);
        state.Pending.Should().Be(0);

        using var scope = fx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.DrawApprovals.AsNoTracking().SingleAsync(a => a.Id == approvalId))
            .Status.Should().Be(ApprovalStatus.Rejected);
    }

    [Fact(DisplayName = "두 승인자가 동시에 눌러도 한 번만 적용된다")]
    public async Task Concurrent_decisions_apply_exactly_once()
    {
        var ev = await fx.CreateActiveDrawAsync();

        var maker = NewClient();
        await maker.LoginAsync("admin");
        await maker.PostFormAsync(
            $"/Draws/Weights?id={ev.Id}", $"/Draws/Weights?id={ev.Id}&handler=Request",
            WeightForm([50, 45, 200, 750], "1등 체감 개선"));

        var approvalId = await PendingIdAsync(ev.Id);

        // 같은 요청을 admin2 가 두 번 동시에 누른다(더블클릭·새로고침 후 재전송).
        // RowVersion 이 동시성 토큰이므로 한 쪽은 반드시 진다 —
        // 진 쪽이 500 으로 터지면 운영자는 무슨 일이 일어났는지 알 수 없다.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new CountdownEvent(2);

        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            var checker = NewClient();
            await checker.LoginAsync("admin2");

            ready.Signal();
            await gate.Task;

            return await checker.PostFormAsync(
                "/Draws/Approvals", "/Draws/Approvals?handler=Approve", new()
                {
                    ["ApprovalId"] = approvalId.ToString(),
                    ["Note"] = "지급 규모 확인함",
                });
        })).ToArray();

        ready.Wait(TimeSpan.FromSeconds(60));
        gate.SetResult();

        var responses = await Task.WhenAll(tasks);

        responses.Should().OnlyContain(
            r => r.StatusCode == HttpStatusCode.Redirect || r.StatusCode == HttpStatusCode.OK,
            "경합에서 진 쪽도 500 이 아니라 '먼저 결정됐다' 는 답을 받아야 한다");

        var state = await StateAsync(ev.Id);
        state.Slot0Weight.Should().Be(50);
        state.VersionCount.Should().Be(2, "확률표 버전이 두 번 올라가면 안 된다");
        state.Pending.Should().Be(0);

        using var scope = fx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.DrawApprovals.AsNoTracking().SingleAsync(a => a.Id == approvalId))
            .Status.Should().Be(ApprovalStatus.Approved);
    }

    [Fact(DisplayName = "읽기전용 계정은 대기 목록은 보지만 결정할 수 없다")]
    public async Task Viewer_can_see_but_not_decide()
    {
        var ev = await fx.CreateActiveDrawAsync();

        var maker = NewClient();
        await maker.LoginAsync("admin");
        await maker.PostFormAsync(
            $"/Draws/Weights?id={ev.Id}", $"/Draws/Weights?id={ev.Id}&handler=Request",
            WeightForm([50, 45, 200, 750], "1등 체감 개선"));

        var approvalId = await PendingIdAsync(ev.Id);

        var viewer = NewClient();
        await viewer.LoginAsync("viewer");

        (await viewer.GetAsync("/Draws/Approvals")).StatusCode.Should().Be(HttpStatusCode.OK,
            "무엇이 대기 중인지는 감추지 않는다");

        AdminClient.RedirectTarget(await viewer.GetAsync($"/Draws/Weights?id={ev.Id}"))
            .Should().StartWith("/Account/Denied", "확률 변경 요청은 편집 권한이 필요하다");

        // 읽기전용 계정에는 결정 폼 자체가 렌더되지 않으므로 다른 페이지의 토큰으로 직접 POST 한다.
        var res = await viewer.PostFormAsync("/Draws", "/Draws/Approvals?handler=Approve", new()
        {
            ["ApprovalId"] = approvalId.ToString(),
        });

        res.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Redirect);
        (await StateAsync(ev.Id)).Slot0Weight.Should().Be(5, "버튼을 숨기는 것은 방어가 아니다");
    }
}

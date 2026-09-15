using System.Net;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using CouponOps.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CouponOps.Tests;

/// <summary>운영툴의 인증·권한·확인 절차·감사 로그를 실제 HTTP 흐름으로 확인한다.</summary>
[Collection(CouponOpsCollection.Name)]
public sealed class AdminToolTests(CouponOpsFixture fx)
{
    private AdminClient NewClient() => new(fx.App);

    // ── 인증 ────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "로그인하지 않으면 운영툴 페이지가 로그인으로 튕긴다")]
    public async Task Anonymous_access_is_redirected_to_login()
    {
        var client = NewClient();

        foreach (var url in (string[])["/Events", "/Issuances", "/OperationLogs"])
        {
            var res = await client.GetAsync(url);
            AdminClient.RedirectTarget(res).Should().StartWith("/Account/Login",
                $"{url} 은 인증이 필요하다");
        }
    }

    [Fact(DisplayName = "비밀번호가 틀리면 로그인되지 않는다")]
    public async Task Wrong_password_does_not_sign_in()
    {
        var client = NewClient();

        var res = await client.LoginAsync("admin", "wrong-password");

        res.StatusCode.Should().Be(HttpStatusCode.OK, "로그인 페이지를 다시 렌더한다");
        (await res.Content.ReadAsStringAsync()).Should().Contain("아이디 또는 비밀번호가 올바르지 않습니다");

        AdminClient.RedirectTarget(await client.GetAsync("/Events"))
            .Should().StartWith("/Account/Login");
    }

    [Fact(DisplayName = "편집 계정으로 로그인하면 이벤트 목록이 열린다")]
    public async Task Editor_can_sign_in_and_see_events()
    {
        var client = NewClient();

        var login = await client.LoginAsync("admin");
        AdminClient.RedirectTarget(login).Should().Be("/Events");

        var page = await client.GetAsync("/Events");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── 권한 구분 ───────────────────────────────────────────────────────────

    [Fact(DisplayName = "읽기전용 계정은 조회는 되지만 생성 화면에 들어갈 수 없다")]
    public async Task Viewer_can_read_but_cannot_open_create_page()
    {
        var client = NewClient();
        await client.LoginAsync("viewer");

        (await client.GetAsync("/Events")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/Issuances")).StatusCode.Should().Be(HttpStatusCode.OK);

        AdminClient.RedirectTarget(await client.GetAsync("/Events/Create"))
            .Should().StartWith("/Account/Denied", "생성은 편집 권한이 필요하다");
    }

    [Fact(DisplayName = "읽기전용 계정의 강제 중단 요청은 거부된다")]
    public async Task Viewer_cannot_suspend()
    {
        var ev = await fx.CreateActiveEventAsync(totalQuantity: 10, perUserLimit: 1);

        var client = NewClient();
        await client.LoginAsync("viewer");

        var res = await client.PostFormAsync(
            $"/Events/Details?id={ev.Id}", $"/Events/Details?id={ev.Id}&handler=Suspend",
            new() { ["Id"] = ev.Id.ToString(), ["ConfirmCode"] = ev.Code, ["Reason"] = "권한 없는 시도" });

        AdminClient.RedirectTarget(res).Should().StartWith("/Account/Denied");

        using var scope = fx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Events.AsNoTracking().SingleAsync(e => e.Id == ev.Id)).SuspendedAt
            .Should().BeNull("읽기전용 계정의 시도로 상태가 바뀌면 안 된다");
    }

    // ── 위험 액션의 확인 절차 ───────────────────────────────────────────────

    [Fact(DisplayName = "확인 코드가 틀리면 강제 중단되지 않는다")]
    public async Task Suspend_requires_the_exact_event_code()
    {
        var ev = await fx.CreateActiveEventAsync(totalQuantity: 10, perUserLimit: 1);

        var client = NewClient();
        await client.LoginAsync("admin");

        var res = await client.PostFormAsync(
            $"/Events/Details?id={ev.Id}", $"/Events/Details?id={ev.Id}&handler=Suspend",
            new() { ["Id"] = ev.Id.ToString(), ["ConfirmCode"] = "wrong-code", ["Reason"] = "실수로 누름" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().Contain("이벤트 코드를 정확히 입력");

        // 발급이 계속 가능해야 한다 — 중단이 실제로 일어나지 않았다는 가장 확실한 증거다.
        (await new IssueClient(fx.App).IssueAsync(ev.Id, "still-ok")).Result
            .Should().Be(nameof(IssueResult.Success));
    }

    [Fact(DisplayName = "중단 사유 없이는 강제 중단되지 않는다")]
    public async Task Suspend_requires_a_reason()
    {
        var ev = await fx.CreateActiveEventAsync(totalQuantity: 10, perUserLimit: 1);

        var client = NewClient();
        await client.LoginAsync("admin");

        var res = await client.PostFormAsync(
            $"/Events/Details?id={ev.Id}", $"/Events/Details?id={ev.Id}&handler=Suspend",
            new() { ["Id"] = ev.Id.ToString(), ["ConfirmCode"] = ev.Code, ["Reason"] = "  " });

        (await res.Content.ReadAsStringAsync()).Should().Contain("중단 사유는 필수");
    }

    [Fact(DisplayName = "강제 중단하면 발급이 즉시 막히고 전/후 값이 운영 로그에 남는다")]
    public async Task Suspend_blocks_issuing_and_is_audited()
    {
        var ev = await fx.CreateActiveEventAsync(totalQuantity: 10, perUserLimit: 1);
        var issueClient = new IssueClient(fx.App);

        (await issueClient.IssueAsync(ev.Id, "before")).Result.Should().Be(nameof(IssueResult.Success));

        var client = NewClient();
        await client.LoginAsync("admin");

        var res = await client.PostFormAsync(
            $"/Events/Details?id={ev.Id}", $"/Events/Details?id={ev.Id}&handler=Suspend",
            new() { ["Id"] = ev.Id.ToString(), ["ConfirmCode"] = ev.Code, ["Reason"] = "코드 유출 정황" });

        AdminClient.RedirectTarget(res).Should().Contain("/Events/Details");

        (await issueClient.IssueAsync(ev.Id, "after")).Result.Should().Be(nameof(IssueResult.Suspended));

        using var scope = fx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var log = await db.OperationLogs.AsNoTracking()
            .Where(o => o.TargetType == nameof(CouponEvent)
                     && o.TargetId == ev.Id.ToString()
                     && o.Action == OperationAction.EventSuspended)
            .SingleAsync();

        log.ActorLoginId.Should().Be("admin");
        log.Reason.Should().Be("코드 유출 정황");
        log.BeforeJson.Should().Contain("\"Suspended\":false");
        log.AfterJson.Should().Contain("\"Suspended\":true");
        log.ChangedFields.Should().Be("Suspended", "바뀐 필드만 요약돼야 한다");

        // 감사 로그는 사람이 읽는 것이 존재 이유다. 한글이 \uXXXX 로 저장되면 쓸모가 없다.
        log.AfterJson.Should().Contain(ev.Name).And.NotContain("\\u");
    }

    [Fact(DisplayName = "재개하면 발급이 다시 허용되고 로그가 남는다")]
    public async Task Resume_restores_issuing()
    {
        var ev = await fx.CreateActiveEventAsync(totalQuantity: 10, perUserLimit: 1);
        await fx.SuspendAsync(ev.Id, "일시 점검");

        var issueClient = new IssueClient(fx.App);
        (await issueClient.IssueAsync(ev.Id, "during")).Result.Should().Be(nameof(IssueResult.Suspended));

        var client = NewClient();
        await client.LoginAsync("admin");

        await client.PostFormAsync(
            $"/Events/Details?id={ev.Id}", $"/Events/Details?id={ev.Id}&handler=Resume",
            new() { ["Id"] = ev.Id.ToString(), ["Reason"] = "점검 완료" });

        (await issueClient.IssueAsync(ev.Id, "after-resume")).Result.Should().Be(nameof(IssueResult.Success));

        using var scope = fx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.OperationLogs.AsNoTracking()
            .AnyAsync(o => o.TargetId == ev.Id.ToString() && o.Action == OperationAction.EventResumed))
            .Should().BeTrue();
    }

    // ── 이벤트 생성·수정 ────────────────────────────────────────────────────

    [Fact(DisplayName = "폼으로 이벤트를 만들면 Redis 까지 준비되고 즉시 발급된다")]
    public async Task Creating_an_event_through_the_form_makes_it_issuable()
    {
        var client = NewClient();
        await client.LoginAsync("admin");

        var code = $"form-{Guid.NewGuid():N}"[..16];
        var startKst = KstNow().AddMinutes(-5);

        var res = await client.PostFormAsync("/Events/Create", "/Events/Create", new()
        {
            ["Code"] = code,
            ["Name"] = "폼으로 만든 이벤트",
            ["StartsAt"] = startKst.ToString("yyyy-MM-ddTHH:mm"),
            ["EndsAt"] = startKst.AddDays(1).ToString("yyyy-MM-ddTHH:mm"),
            ["TotalQuantity"] = "25",
            ["PerUserLimit"] = "1",
            ["IssuanceMode"] = "PreGenerated",
        });

        AdminClient.RedirectTarget(res).Should().Contain("/Events/Details");

        using var scope = fx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var created = await db.Events.AsNoTracking().SingleAsync(e => e.Code == code);

        created.PoolWarmedAt.Should().NotBeNull("생성 시 Redis 워밍업까지 끝나야 한다");
        (await db.Coupons.CountAsync(c => c.EventId == created.Id)).Should().Be(25);

        (await new IssueClient(fx.App).IssueAsync(created.Id, "fresh-user")).Result
            .Should().Be(nameof(IssueResult.Success));

        // 생성도 감사 대상이다.
        (await db.OperationLogs.AsNoTracking()
            .AnyAsync(o => o.TargetId == created.Id.ToString() && o.Action == OperationAction.EventCreated))
            .Should().BeTrue();
    }

    [Fact(DisplayName = "종료시각이 시작시각보다 빠르면 생성되지 않는다")]
    public async Task Invalid_period_is_rejected_by_the_form()
    {
        var client = NewClient();
        await client.LoginAsync("admin");

        var start = KstNow().AddHours(2);
        var res = await client.PostFormAsync("/Events/Create", "/Events/Create", new()
        {
            ["Code"] = $"bad-{Guid.NewGuid():N}"[..14],
            ["Name"] = "거꾸로 이벤트",
            ["StartsAt"] = start.ToString("yyyy-MM-ddTHH:mm"),
            ["EndsAt"] = start.AddHours(-1).ToString("yyyy-MM-ddTHH:mm"),
            ["TotalQuantity"] = "10",
            ["PerUserLimit"] = "1",
            ["IssuanceMode"] = "PreGenerated",
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().Contain("종료 시각은 시작 시각보다 뒤여야 합니다");
    }

    [Fact(DisplayName = "진행 중 이벤트의 수량 감소는 거부된다")]
    public async Task Reducing_quantity_on_a_running_event_is_refused()
    {
        var ev = await fx.CreateActiveEventAsync(totalQuantity: 50, perUserLimit: 1);

        var client = NewClient();
        await client.LoginAsync("admin");

        var res = await client.PostFormAsync(
            $"/Events/Edit?id={ev.Id}", $"/Events/Edit?id={ev.Id}", new()
            {
                ["Id"] = ev.Id.ToString(),
                ["Name"] = ev.Name,
                ["StartsAt"] = CouponOps.Application.KoreaTime.ToInput(ev.StartsAt),
                ["EndsAt"] = CouponOps.Application.KoreaTime.ToInput(ev.EndsAt),
                ["TotalQuantity"] = "10",
                ["PerUserLimit"] = "1",
            });

        (await res.Content.ReadAsStringAsync()).Should().Contain("수량은 줄일 수 없습니다");

        using var scope = fx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Events.AsNoTracking().SingleAsync(e => e.Id == ev.Id)).TotalQuantity.Should().Be(50);
    }

    [Fact(DisplayName = "진행 중 이벤트를 증량하면 늘어난 만큼 더 발급된다")]
    public async Task Increasing_quantity_on_a_running_event_adds_stock()
    {
        var ev = await fx.CreateActiveEventAsync(totalQuantity: 5, perUserLimit: 1);
        var issueClient = new IssueClient(fx.App);

        var first = await issueClient.IssueConcurrentlyAsync(ev.Id, TestData.DistinctUsers(6, "grow-a"));
        first.Count(c => c.Result == nameof(IssueResult.Success)).Should().Be(5);

        var client = NewClient();
        await client.LoginAsync("admin");

        await client.PostFormAsync($"/Events/Edit?id={ev.Id}", $"/Events/Edit?id={ev.Id}", new()
        {
            ["Id"] = ev.Id.ToString(),
            ["Name"] = ev.Name,
            ["StartsAt"] = CouponOps.Application.KoreaTime.ToInput(ev.StartsAt),
            ["EndsAt"] = CouponOps.Application.KoreaTime.ToInput(ev.EndsAt),
            ["TotalQuantity"] = "8",
            ["PerUserLimit"] = "1",
            ["Reason"] = "물량 추가",
        });

        // 정확히 3장만 더 나가야 한다. 워밍업이 풀을 통째로 다시 만들면 5장이 되살아난다.
        var second = await issueClient.IssueConcurrentlyAsync(ev.Id, TestData.DistinctUsers(10, "grow-b"));
        second.Count(c => c.Result == nameof(IssueResult.Success)).Should().Be(3);
    }

    // ── 현황 API ────────────────────────────────────────────────────────────

    [Fact(DisplayName = "현황 API 가 소진율과 적재 지연 건수를 돌려준다")]
    public async Task Status_endpoint_reports_consumption()
    {
        var ev = await fx.CreateActiveEventAsync(totalQuantity: 10, perUserLimit: 1);
        await new IssueClient(fx.App).IssueConcurrentlyAsync(ev.Id, TestData.DistinctUsers(4, "status"));

        var client = NewClient();
        await client.LoginAsync("viewer");   // 읽기전용도 현황은 볼 수 있어야 한다

        var json = await client.GetStringAsync($"/api/events/{ev.Id}/status");

        json.Should().Contain("\"issued\":4");
        json.Should().Contain("\"remaining\":6");
        json.Should().Contain("\"consumedRate\":40");
    }

    [Fact(DisplayName = "현황 API 는 로그인하지 않으면 접근할 수 없다")]
    public async Task Status_endpoint_requires_authentication()
    {
        var ev = await fx.CreateActiveEventAsync(totalQuantity: 10, perUserLimit: 1);

        var res = await NewClient().GetAsync($"/api/events/{ev.Id}/status");

        AdminClient.RedirectTarget(res).Should().StartWith("/Account/Login");
    }

    // ── 목록 필터 ───────────────────────────────────────────────────────────

    [Fact(DisplayName = "이벤트 목록의 상태 필터가 동작한다")]
    public async Task Event_list_filters_by_status()
    {
        var now = DateTime.UtcNow;
        var active = await fx.CreateActiveEventAsync(totalQuantity: 5, perUserLimit: 1);
        var future = await fx.CreateEventAsync(now.AddDays(3), now.AddDays(4));

        var client = NewClient();
        await client.LoginAsync("viewer");

        var activeHtml = await client.GetStringAsync("/Events?Status=Active");
        activeHtml.Should().Contain(active.Code);
        activeHtml.Should().NotContain(future.Code);

        var scheduledHtml = await client.GetStringAsync("/Events?Status=Scheduled");
        scheduledHtml.Should().Contain(future.Code);
        scheduledHtml.Should().NotContain(active.Code);
    }

    [Fact(DisplayName = "로그인 감사 로그에 실제 로그인한 계정이 기록된다")]
    public async Task Sign_in_is_audited_with_the_real_actor()
    {
        var client = NewClient();
        await client.LoginAsync("viewer");

        using var scope = fx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var log = await db.OperationLogs.AsNoTracking()
            .Where(o => o.Action == OperationAction.AdminSignedIn)
            .OrderByDescending(o => o.Id)
            .FirstAsync();

        // SignInAsync 는 응답 쿠키만 세팅하므로, principal 을 HttpContext 에 꽂지 않으면
        // 여기가 "system" 으로 남는다.
        log.ActorLoginId.Should().Be("viewer");
        log.ActorId.Should().BeGreaterThan(0);
    }

    [Fact(DisplayName = "로그아웃하면 세션이 끊긴다")]
    public async Task Logout_ends_the_session()
    {
        var client = NewClient();
        await client.LoginAsync("admin");
        (await client.GetAsync("/Events")).StatusCode.Should().Be(HttpStatusCode.OK);

        // 레이아웃의 로그아웃 폼에서 토큰을 받아 온다 — 토큰이 빠져 있으면 여기서 실패한다.
        await client.PostFormAsync("/Events", "/Account/Logout", []);

        AdminClient.RedirectTarget(await client.GetAsync("/Events"))
            .Should().StartWith("/Account/Login");
    }

    private static DateTime KstNow() =>
        CouponOps.Application.KoreaTime.FromUtc(DateTime.UtcNow);
}

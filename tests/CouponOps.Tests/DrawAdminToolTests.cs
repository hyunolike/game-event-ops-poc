using System.Net;
using CouponOps.Application;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using CouponOps.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CouponOps.Tests;

/// <summary>룰렛 운영툴의 인증·권한·시뮬레이션 게이트·확인 절차를 실제 HTTP 흐름으로 확인한다.</summary>
[Collection(CouponOpsCollection.Name)]
public sealed class DrawAdminToolTests(CouponOpsFixture fx)
{
    private AdminClient NewClient() => new(fx.App);

    private static DateTime KstNow() => KoreaTime.FromUtc(DateTime.UtcNow);

    private static Dictionary<string, string> StandardForm(string code, DateTime startKst) => new()
    {
        ["Code"] = code,
        ["Name"] = "폼으로 만든 룰렛",
        ["StartsAt"] = startKst.ToString("yyyy-MM-ddTHH:mm"),
        ["EndsAt"] = startKst.AddDays(7).ToString("yyyy-MM-ddTHH:mm"),
        ["DailyDrawLimit"] = "3",
        ["DailyResetHour"] = "4",
        ["TicketItemId"] = "",
        ["TicketCost"] = "0",
        ["PityThreshold"] = "0",
        ["PitySlot"] = "-1",
        ["FallbackSlot"] = "2",
        ["SimulationDraws"] = "10000",

        ["Slots[0].Name"] = "전설 무기 상자",
        ["Slots[0].ItemId"] = "9001",
        ["Slots[0].ItemQty"] = "1",
        ["Slots[0].Weight"] = "5",
        ["Slots[0].Stock"] = "10",
        ["Slots[0].IsJackpot"] = "true",
        ["Slots[0].IsBlank"] = "false",

        ["Slots[1].Name"] = "강화 주문서",
        ["Slots[1].ItemId"] = "3001",
        ["Slots[1].ItemQty"] = "5",
        ["Slots[1].Weight"] = "245",
        ["Slots[1].Stock"] = "-1",
        ["Slots[1].IsJackpot"] = "false",
        ["Slots[1].IsBlank"] = "false",

        ["Slots[2].Name"] = "골드",
        ["Slots[2].ItemId"] = "1001",
        ["Slots[2].ItemQty"] = "1000",
        ["Slots[2].Weight"] = "750",
        ["Slots[2].Stock"] = "-1",
        ["Slots[2].IsJackpot"] = "false",
        ["Slots[2].IsBlank"] = "false",
    };

    [Fact(DisplayName = "로그인하지 않으면 룰렛 운영 화면이 로그인으로 튕긴다")]
    public async Task Anonymous_access_to_draw_pages_is_redirected()
    {
        var client = NewClient();

        foreach (var url in (string[])["/Draws", "/Draws/Create"])
            AdminClient.RedirectTarget(await client.GetAsync(url))
                .Should().StartWith("/Account/Login", $"{url} 은 인증이 필요하다");
    }

    [Fact(DisplayName = "확률 공시 페이지는 로그인 없이 열린다")]
    public async Task Odds_page_is_public()
    {
        var ev = await fx.CreateActiveDrawAsync();

        // 로그인하지 않은 순수 클라이언트로 연다 — 공시가 로그인 뒤에 있으면 공시가 아니다.
        var html = await fx.App.CreateClient().GetStringAsync($"/Odds?code={ev.Code}");

        html.Should().Contain("확률 정보");
        html.Should().Contain("0.50%", "표준 구성의 잭팟 확률");
        html.Should().Contain("100.00%", "합계는 반올림 잔차 보정으로 항상 정확히 100.00% 여야 한다");
    }

    [Fact(DisplayName = "읽기전용 계정은 룰렛 목록은 보지만 생성 화면에 못 들어간다")]
    public async Task Viewer_can_read_but_cannot_create()
    {
        var client = NewClient();
        await client.LoginAsync("viewer");

        (await client.GetAsync("/Draws")).StatusCode.Should().Be(HttpStatusCode.OK);

        AdminClient.RedirectTarget(await client.GetAsync("/Draws/Create"))
            .Should().StartWith("/Account/Denied", "생성은 편집 권한이 필요하다");
    }

    [Fact(DisplayName = "시뮬레이션을 보지 않고 바로 생성하면 거부된다")]
    public async Task Creating_without_seeing_the_simulation_is_blocked()
    {
        var client = NewClient();
        await client.LoginAsync("admin");

        var code = $"gate-{Guid.NewGuid():N}"[..16];
        var res = await client.PostFormAsync(
            "/Draws/Create", "/Draws/Create?handler=Create", StandardForm(code, KstNow().AddMinutes(-5)));

        res.StatusCode.Should().Be(HttpStatusCode.OK, "리다이렉트 없이 폼을 다시 렌더한다");
        (await res.Content.ReadAsStringAsync())
            .Should().Contain("시뮬레이션 결과를 확인한 뒤 생성하세요");

        using var scope = fx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.DrawEvents.AnyAsync(e => e.Code == code)).Should().BeFalse("게이트를 통과하지 못했다");
    }

    [Fact(DisplayName = "시뮬레이션을 거치면 룰렛이 만들어지고 즉시 추첨된다")]
    public async Task Previewing_then_creating_makes_it_spinnable()
    {
        var client = NewClient();
        await client.LoginAsync("admin");

        var code = $"form-{Guid.NewGuid():N}"[..16];
        var form = StandardForm(code, KstNow().AddMinutes(-5));

        // 1) 시뮬레이션. 응답 HTML 에 게이트 서명이 심어져 돌아온다.
        var preview = await client.PostFormAsync(
            "/Draws/Create", "/Draws/Create?handler=Preview", new Dictionary<string, string>(form));
        var previewHtml = await preview.Content.ReadAsStringAsync();

        previewHtml.Should().Contain("확률 미리보기");
        previewHtml.Should().Contain("0.50%", "가중치 5 / 1000 → 0.50%");

        var signature = ExtractSignature(previewHtml);

        // 2) 같은 설정 + 서명으로 생성.
        form["PreviewSignature"] = signature;
        var created = await client.PostFormAsync(
            "/Draws/Create", "/Draws/Create?handler=Create", form);

        AdminClient.RedirectTarget(created).Should().Contain("/Draws/Details");

        using var scope = fx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ev = await db.DrawEvents.AsNoTracking().SingleAsync(e => e.Code == code);

        ev.PoolWarmedAt.Should().NotBeNull("생성 시 Redis 워밍업까지 끝나야 한다");
        ev.ActiveWeightVersionId.Should().NotBeNull("확률표 버전이 활성화돼야 한다");
        (await db.DrawPrizes.CountAsync(p => p.DrawEventId == ev.Id)).Should().Be(3);

        (await new DrawClient(fx.App).SpinAsync(ev.Id, "fresh-user")).Result
            .Should().Be(nameof(DrawResult.Won));
    }

    [Fact(DisplayName = "강제 중단은 이벤트 코드를 정확히 입력해야 한다")]
    public async Task Suspending_requires_typing_the_event_code()
    {
        var ev = await fx.CreateActiveDrawAsync();
        var client = NewClient();
        await client.LoginAsync("admin");

        var wrong = await client.PostFormAsync(
            $"/Draws/Details?id={ev.Id}", $"/Draws/Details?id={ev.Id}&handler=Suspend", new()
            {
                ["ConfirmCode"] = "틀린-코드",
                ["Reason"] = "확률 설정 오류 의심",
            });

        wrong.StatusCode.Should().Be(HttpStatusCode.OK);
        (await wrong.Content.ReadAsStringAsync()).Should().Contain("이벤트 코드를 정확히 입력");

        using (var scope = fx.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.DrawEvents.AsNoTracking().SingleAsync(e => e.Id == ev.Id))
                .SuspendedAt.Should().BeNull("잘못된 확인 입력으로 중단되면 안 된다");
        }

        var right = await client.PostFormAsync(
            $"/Draws/Details?id={ev.Id}", $"/Draws/Details?id={ev.Id}&handler=Suspend", new()
            {
                ["ConfirmCode"] = ev.Code,
                ["Reason"] = "확률 설정 오류 의심",
            });

        AdminClient.RedirectTarget(right).Should().Contain("/Draws/Details");
        (await new DrawClient(fx.App).SpinAsync(ev.Id, "after")).Result
            .Should().Be(nameof(DrawResult.Suspended));
    }

    [Fact(DisplayName = "중단 사유 없이는 중단할 수 없다")]
    public async Task Suspending_requires_a_reason()
    {
        var ev = await fx.CreateActiveDrawAsync();
        var client = NewClient();
        await client.LoginAsync("admin");

        var res = await client.PostFormAsync(
            $"/Draws/Details?id={ev.Id}", $"/Draws/Details?id={ev.Id}&handler=Suspend", new()
            {
                ["ConfirmCode"] = ev.Code,
                ["Reason"] = "   ",
            });

        (await res.Content.ReadAsStringAsync()).Should().Contain("중단 사유는 필수");
    }

    /// <summary>시뮬레이션 응답에 심긴 게이트 서명을 꺼낸다. 브라우저가 그대로 되돌려 보내는 값이다.</summary>
    private static string ExtractSignature(string html)
    {
        const string attr = """name="PreviewSignature" value=""";

        var idx = html.IndexOf(attr, StringComparison.Ordinal);
        idx.Should().BeGreaterThan(-1, "시뮬레이션 응답에 게이트 서명이 들어 있어야 한다");

        var start = idx + attr.Length + 1;   // 여는 따옴표 다음
        var end = html.IndexOf('"', start);
        return html[start..end];
    }
}

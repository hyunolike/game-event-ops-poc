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
/// 보상 우편함 — 지급의 마지막 구간.
/// </summary>
/// <remarks>
/// 우편함을 두는 값은 <b>미수령분을 되돌릴 수 있다</b> 는 데 있다.
/// 인벤토리에 직접 꽂았다면 잘못 설정된 경품을 회수할 방법이 없다.
/// </remarks>
[Collection(CouponOpsCollection.Name)]
public sealed class DrawMailTests(CouponOpsFixture fx)
{
    private DrawClient Draws => new(fx.App);
    private MailClient Mails => new(fx.App);

    /// <summary>꽝 없이 전부 지급되는 구성 — 추첨하면 반드시 우편이 한 통 생긴다.</summary>
    private static List<DrawPrize> AlwaysGrantPrizes() =>
    [
        DrawPrize.Create(0, "강화 주문서", 3001, 5,
            weight: 1, initialStock: DrawPrize.Unlimited, isJackpot: false, isBlank: false),
        DrawPrize.Create(1, "골드", 1001, 1000,
            weight: 1, initialStock: DrawPrize.Unlimited, isJackpot: false, isBlank: false),
    ];

    [Fact(DisplayName = "당첨하면 우편이 생기고, 수령하면 확정된다")]
    public async Task Winning_creates_a_mail_that_can_be_claimed()
    {
        var ev = await fx.CreateActiveDrawAsync(
            prizes: AlwaysGrantPrizes(), dailyDrawLimit: 1, ticketItemId: null, ticketCost: 0);

        var spin = await Draws.SpinAsync(ev.Id, "mail-user");
        spin.Result.Should().Be(nameof(DrawResult.Won));

        await fx.WaitForMailsAsync(ev.Id, "mail-user", 1, TimeSpan.FromSeconds(30));

        var inbox = await Mails.ListAsync(ev.Id, "mail-user");
        inbox.Should().HaveCount(1);
        inbox[0].PrizeName.Should().Be(spin.Body!.Prize!.Name);
        inbox[0].Qty.Should().Be(spin.Body.Prize.Qty);

        var claim = await Mails.ClaimAsync(ev.Id, inbox[0].MailId, "mail-user");
        claim.Result.Should().Be(nameof(MailClaimOutcome.Claimed));
        claim.Status.Should().Be(HttpStatusCode.OK);

        (await Mails.ListAsync(ev.Id, "mail-user")).Should().BeEmpty("수령한 우편은 목록에서 빠진다");
    }

    [Fact(DisplayName = "같은 우편을 동시에 여러 번 수령해도 정확히 한 번만 성공한다")]
    public async Task Concurrent_claims_succeed_exactly_once()
    {
        var ev = await fx.CreateActiveDrawAsync(
            prizes: AlwaysGrantPrizes(), dailyDrawLimit: 1, ticketItemId: null, ticketCost: 0);

        (await Draws.SpinAsync(ev.Id, "double-click")).Result.Should().Be(nameof(DrawResult.Won));
        await fx.WaitForMailsAsync(ev.Id, "double-click", 1, TimeSpan.FromSeconds(30));

        var mailId = (await Mails.ListAsync(ev.Id, "double-click"))[0].MailId;

        var calls = await Mails.ClaimConcurrentlyAsync(ev.Id, mailId, "double-click", attempts: 10);

        calls.Count(c => c.Result == nameof(MailClaimOutcome.Claimed)).Should().Be(1,
            "한 통의 우편이 두 번 지급되면 안 된다");
        calls.Count(c => c.Result == nameof(MailClaimOutcome.AlreadyClaimed)).Should().Be(9);
        calls.Should().OnlyContain(c => c.Status == HttpStatusCode.OK,
            "중복 클릭은 오류가 아니라 이미 달성된 상태다");
    }

    [Fact(DisplayName = "남의 우편은 조회도 수령도 되지 않는다")]
    public async Task Another_users_mail_is_not_reachable()
    {
        var ev = await fx.CreateActiveDrawAsync(
            prizes: AlwaysGrantPrizes(), dailyDrawLimit: 1, ticketItemId: null, ticketCost: 0);

        await Draws.SpinAsync(ev.Id, "owner");
        await fx.WaitForMailsAsync(ev.Id, "owner", 1, TimeSpan.FromSeconds(30));
        var mailId = (await Mails.ListAsync(ev.Id, "owner"))[0].MailId;

        (await Mails.ListAsync(ev.Id, "stranger")).Should().BeEmpty();

        var claim = await Mails.ClaimAsync(ev.Id, mailId, "stranger");
        claim.Result.Should().Be(nameof(MailClaimOutcome.NotFound),
            "'남의 것' 과 '없는 것' 을 구분해 알리면 우편 존재 여부를 열거할 수 있다");
        claim.Status.Should().Be(HttpStatusCode.NotFound);

        (await Mails.ListAsync(ev.Id, "owner")).Should().HaveCount(1, "주인의 우편은 그대로다");
    }

    [Fact(DisplayName = "운영자 회수는 미수령분만 되돌리고 수령분은 건드리지 않는다")]
    public async Task Revoking_only_touches_unclaimed_mail()
    {
        var ev = await fx.CreateActiveDrawAsync(
            prizes: AlwaysGrantPrizes(), dailyDrawLimit: 1, ticketItemId: null, ticketCost: 0);

        // 두 유저가 당첨되고, 한 명만 수령한다.
        foreach (var user in (string[])["claimed-user", "pending-user"])
        {
            (await Draws.SpinAsync(ev.Id, user)).Result.Should().Be(nameof(DrawResult.Won));
            await fx.WaitForMailsAsync(ev.Id, user, 1, TimeSpan.FromSeconds(30));
        }

        var claimedMail = (await Mails.ListAsync(ev.Id, "claimed-user"))[0];
        (await Mails.ClaimAsync(ev.Id, claimedMail.MailId, "claimed-user")).Result
            .Should().Be(nameof(MailClaimOutcome.Claimed));

        var pendingMail = (await Mails.ListAsync(ev.Id, "pending-user"))[0];

        int revoked;
        using (var scope = fx.CreateScope())
        {
            var mails = scope.ServiceProvider.GetRequiredService<DrawMailService>();
            revoked = await mails.RevokeUnclaimedAsync(ev.Id, prizeId: null,
                reason: "경품 수량 오설정", CancellationToken.None);
        }

        revoked.Should().Be(1, "미수령 한 통만 회수돼야 한다");

        (await Mails.ListAsync(ev.Id, "pending-user")).Should().BeEmpty();
        var afterRevoke = await Mails.ClaimAsync(ev.Id, pendingMail.MailId, "pending-user");
        afterRevoke.Result.Should().Be(nameof(MailClaimOutcome.Revoked));
        afterRevoke.Status.Should().Be(HttpStatusCode.Conflict);

        using var check = fx.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();

        (await db.DrawRewardMails.AsNoTracking().SingleAsync(m => m.Id == claimedMail.MailId))
            .RevokedAt.Should().BeNull("이미 수령한 우편은 회수할 수 없다 — 아이템이 이미 유저 손에 있다");

        (await db.OperationLogs.AsNoTracking()
            .AnyAsync(o => o.TargetType == nameof(DrawEvent)
                        && o.TargetId == ev.Id.ToString()
                        && o.Action == OperationAction.DrawRewardsRevoked))
            .Should().BeTrue("회수는 감사 대상이다");
    }

    [Fact(DisplayName = "회수 사유 없이는 회수할 수 없다")]
    public async Task Revoking_requires_a_reason()
    {
        var ev = await fx.CreateActiveDrawAsync(
            prizes: AlwaysGrantPrizes(), dailyDrawLimit: 1, ticketItemId: null, ticketCost: 0);

        using var scope = fx.CreateScope();
        var mails = scope.ServiceProvider.GetRequiredService<DrawMailService>();

        var act = () => mails.RevokeUnclaimedAsync(ev.Id, null, "   ", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact(DisplayName = "꽝은 우편을 만들지 않는다")]
    public async Task Blank_results_create_no_mail()
    {
        var ev = await fx.CreateActiveDrawAsync(
            prizes: DrawTestData.BlankHeavyPrizes(),
            dailyDrawLimit: 3, ticketItemId: null, ticketCost: 0);

        for (var i = 0; i < 3; i++)
        {
            var spin = await Draws.SpinAsync(ev.Id, "blank-user");
            spin.Body!.Prize!.IsBlank.Should().BeTrue();
        }

        await fx.WaitForDrawLogsAsync(ev.Id, 3, TimeSpan.FromSeconds(30));

        (await Mails.ListAsync(ev.Id, "blank-user")).Should().BeEmpty("보낼 것이 없다");
    }
}

using CouponOps.Application;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using CouponOps.Infrastructure.Redis;
using CouponOps.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CouponOps.Tests;

/// <summary>
/// 이상 탐지 — 부정 판정이 아니라 "봐야 할 것" 을 띄운다.
/// </summary>
/// <remarks>
/// 임계치 미만을 조용히 넘기는 것도 같은 비중으로 확인한다.
/// 정상 이용자가 매번 목록에 뜨면 운영자는 그 화면을 보지 않게 되고, 그러면 탐지는 없는 것과 같다.
/// </remarks>
[Collection(CouponOpsCollection.Name)]
public sealed class DrawAnomalyTests(CouponOpsFixture fx)
{
    private async Task<IReadOnlyList<AnomalyRow>> ScanAsync(long drawEventId)
    {
        using var scope = fx.CreateScope();
        var detector = scope.ServiceProvider.GetRequiredService<DrawAnomalyDetector>();
        return await detector.ScanAsync(drawEventId, CancellationToken.None);
    }

    /// <summary>슬롯 0 이 잭팟인 구성. 재고를 넉넉히 줘 대체 치환이 끼어들지 않게 한다.</summary>
    private static List<DrawPrize> JackpotAlwaysPrizes() => DrawTestData.ScarcePrizes(stock: 100);

    /// <summary>
    /// 난수 0 은 roll 0 이 되어 항상 슬롯 0(잭팟)을 뽑는다.
    /// 확률에 기대는 단언은 언제 깨져도 이상하지 않고, 그러면 아무도 그 테스트를 믿지 않는다.
    /// </summary>
    private const long AlwaysSlotZero = 0;

    [Fact(DisplayName = "같은 유저의 잭팟 반복 당첨을 띄운다")]
    public async Task Repeated_jackpot_wins_are_surfaced()
    {
        var ev = await fx.CreateActiveDrawAsync(
            prizes: JackpotAlwaysPrizes(),
            dailyDrawLimit: 10, ticketItemId: null, ticketCost: 0);

        // 기본 임계치는 3회다. 5회 당첨시켜 확실히 넘긴다.
        for (var i = 0; i < 5; i++)
            (await fx.SpinDirectAsync(ev.Id, "lucky-one", AlwaysSlotZero)).Result
                .Should().Be(DrawResult.Won);

        await fx.WaitForDrawLogsAsync(ev.Id, 5, TimeSpan.FromSeconds(30));

        var rows = await ScanAsync(ev.Id);

        var jackpot = rows.Where(r => r.Kind == AnomalyKind.JackpotRepeat).ToList();
        jackpot.Should().ContainSingle();
        jackpot[0].UserId.Should().Be("lucky-one");
        jackpot[0].Count.Should().Be(5);
        jackpot[0].Detail.Should().Contain("잭팟");
    }

    [Fact(DisplayName = "임계치 미만은 띄우지 않는다")]
    public async Task Below_the_threshold_nothing_is_surfaced()
    {
        var ev = await fx.CreateActiveDrawAsync(
            prizes: JackpotAlwaysPrizes(),
            dailyDrawLimit: 10, ticketItemId: null, ticketCost: 0);

        // 2회 — 기본 임계치 3회 미만이다.
        for (var i = 0; i < 2; i++)
            await fx.SpinDirectAsync(ev.Id, "ordinary", AlwaysSlotZero);

        await fx.WaitForDrawLogsAsync(ev.Id, 2, TimeSpan.FromSeconds(30));

        (await ScanAsync(ev.Id))
            .Where(r => r.Kind == AnomalyKind.JackpotRepeat)
            .Should().BeEmpty("정상 이용자가 매번 뜨면 운영자는 그 화면을 보지 않게 된다");
    }

    [Fact(DisplayName = "잭팟이 없는 이벤트에서는 반복 당첨을 보지 않는다")]
    public async Task Events_without_a_jackpot_report_no_repeats()
    {
        // 표준 구성에서 잭팟 표시를 뗀 슬롯만 쓴다.
        List<DrawPrize> noJackpot =
        [
            DrawPrize.Create(0, "강화 주문서", 3001, 5,
                weight: 1, initialStock: DrawPrize.Unlimited, isJackpot: false, isBlank: false),
            DrawPrize.Create(1, "골드", 1001, 1000,
                weight: 1, initialStock: DrawPrize.Unlimited, isJackpot: false, isBlank: false),
        ];

        var ev = await fx.CreateActiveDrawAsync(
            prizes: noJackpot, dailyDrawLimit: 10, ticketItemId: null, ticketCost: 0);

        for (var i = 0; i < 5; i++)
            await fx.SpinDirectAsync(ev.Id, "frequent", AlwaysSlotZero);

        await fx.WaitForDrawLogsAsync(ev.Id, 5, TimeSpan.FromSeconds(30));

        (await ScanAsync(ev.Id))
            .Where(r => r.Kind == AnomalyKind.JackpotRepeat)
            .Should().BeEmpty("잭팟으로 표시된 경품이 없으면 볼 대상 자체가 없다");
    }

    [Fact(DisplayName = "짧은 시간에 몰린 추첨을 띄운다")]
    public async Task A_burst_of_draws_is_surfaced()
    {
        var ev = await fx.CreateActiveDrawAsync(
            prizes: JackpotAlwaysPrizes(),
            dailyDrawLimit: 20, ticketItemId: null, ticketCost: 0);

        for (var i = 0; i < 6; i++)
            await fx.SpinDirectAsync(ev.Id, "macro-user", AlwaysSlotZero);

        await fx.WaitForDrawLogsAsync(ev.Id, 6, TimeSpan.FromSeconds(30));

        // 임계치를 이 테스트에서만 낮춘다. 기본값(5분에 100회)을 실제로 채우려면 100번을 돌려야 하고,
        // 그 시간은 이 단언이 확인하려는 것과 아무 상관이 없다.
        using var scope = fx.CreateScope();
        var detector = new DrawAnomalyDetector(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            Options.Create(new DrawOptions { BurstThreshold = 5 }),
            scope.ServiceProvider.GetRequiredService<TimeProvider>());

        var rows = await detector.ScanAsync(ev.Id, CancellationToken.None);

        var burst = rows.Where(r => r.Kind == AnomalyKind.DrawBurst).ToList();
        burst.Should().ContainSingle();
        burst[0].UserId.Should().Be("macro-user");
        burst[0].Count.Should().BeGreaterThanOrEqualTo(6);
        burst[0].Detail.Should().Contain("시도");
    }
}

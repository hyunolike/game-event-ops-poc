using CouponOps.Application;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using CouponOps.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CouponOps.Tests.Infrastructure;

public static class DrawTestData
{
    /// <summary>
    /// 표준 4슬롯 구성. 잭팟(유한 재고) → 영웅(유한) → 소모품(무제한) → 꽝(무제한).
    /// 유한 재고 슬롯은 모두 무제한 슬롯을 대체 대상으로 가리킨다.
    /// </summary>
    public static List<DrawPrize> StandardPrizes(int jackpotStock = 10, int heroStock = 100) =>
    [
        DrawPrize.Create(0, "전설 무기 상자", itemId: 9001, itemQty: 1,
            weight: 5, initialStock: jackpotStock, isJackpot: true, isBlank: false, fallbackSlotIndex: 3),
        DrawPrize.Create(1, "영웅 무기 상자", itemId: 9002, itemQty: 1,
            weight: 45, initialStock: heroStock, isJackpot: false, isBlank: false, fallbackSlotIndex: 3),
        DrawPrize.Create(2, "강화 주문서", itemId: 3001, itemQty: 5,
            weight: 200, initialStock: DrawPrize.Unlimited, isJackpot: false, isBlank: false),
        DrawPrize.Create(3, "골드", itemId: 1001, itemQty: 1000,
            weight: 750, initialStock: DrawPrize.Unlimited, isJackpot: false, isBlank: false),
    ];

    /// <summary>
    /// 한정 경품이 거의 확실히 뽑히는 구성. 동시성·대체 치환 테스트용이다.
    /// 슬롯 0 의 가중치가 압도적이라, 재고가 남아 있는 동안은 사실상 전부 슬롯 0 이 나온다 —
    /// 그래서 "재고를 넘긴 지급이 0건" 을 적은 요청 수로 증명할 수 있다.
    /// </summary>
    public static List<DrawPrize> ScarcePrizes(int stock) =>
    [
        DrawPrize.Create(0, "한정 상자", itemId: 9001, itemQty: 1,
            weight: 999_999, initialStock: stock, isJackpot: true, isBlank: false, fallbackSlotIndex: 1),
        DrawPrize.Create(1, "골드", itemId: 1001, itemQty: 100,
            weight: 1, initialStock: DrawPrize.Unlimited, isJackpot: false, isBlank: false),
    ];

    /// <summary>가중치가 균등하지 않은 4슬롯 무제한 구성. 분포 검정용.</summary>
    public static List<DrawPrize> UnlimitedFourPrizes() =>
    [
        DrawPrize.Create(0, "A", 1, 1, weight: 100, initialStock: DrawPrize.Unlimited, isJackpot: false, isBlank: false),
        DrawPrize.Create(1, "B", 2, 1, weight: 200, initialStock: DrawPrize.Unlimited, isJackpot: false, isBlank: false),
        DrawPrize.Create(2, "C", 3, 1, weight: 300, initialStock: DrawPrize.Unlimited, isJackpot: false, isBlank: false),
        DrawPrize.Create(3, "D", 4, 1, weight: 400, initialStock: DrawPrize.Unlimited, isJackpot: false, isBlank: false),
    ];

    /// <summary>꽝이 포함된 구성. 천장 테스트용 — 꽝 가중치를 압도적으로 준다.</summary>
    public static List<DrawPrize> BlankHeavyPrizes() =>
    [
        DrawPrize.Create(0, "영웅 무기 상자", itemId: 9002, itemQty: 1,
            weight: 1, initialStock: DrawPrize.Unlimited, isJackpot: false, isBlank: false),
        DrawPrize.Create(1, "꽝", itemId: 0, itemQty: 0,
            weight: 999_999, initialStock: DrawPrize.Unlimited, isJackpot: false, isBlank: true),
    ];

    public static async Task<DrawEvent> CreateActiveDrawAsync(
        this CouponOpsFixture fx,
        IReadOnlyList<DrawPrize>? prizes = null,
        int dailyDrawLimit = 0,
        long? ticketItemId = 7001,
        int ticketCost = 1,
        int pityThreshold = 0,
        int? pitySlotIndex = null,
        DateTime? startsAtUtc = null,
        DateTime? endsAtUtc = null)
    {
        using var scope = fx.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<DrawAdminService>();
        var now = DateTime.UtcNow;

        return await admin.CreateAndWarmAsync(
            code: $"drw-{Guid.NewGuid():N}"[..20],
            name: "테스트 룰렛",
            startsAtUtc: startsAtUtc ?? now.AddMinutes(-1),
            endsAtUtc: endsAtUtc ?? now.AddHours(1),
            dailyDrawLimit: dailyDrawLimit,
            dailyResetAt: TimeSpan.FromHours(4),
            ticketItemId: ticketItemId,
            ticketCost: ticketCost,
            pityThreshold: pityThreshold,
            pitySlotIndex: pitySlotIndex,
            prizes: prizes ?? StandardPrizes(),
            ct: CancellationToken.None);
    }

    public static async Task GrantTicketsAsync(
        this CouponOpsFixture fx, long drawEventId, string userId, int amount)
    {
        using var scope = fx.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDrawStore>();
        await store.GrantTicketsAsync(drawEventId, userId, amount, CancellationToken.None);
    }

    public static async Task SuspendDrawAsync(this CouponOpsFixture fx, long drawEventId, string reason)
    {
        using var scope = fx.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<DrawAdminService>();
        await admin.SuspendAsync(drawEventId, reason, CancellationToken.None);
    }

    public static async Task<IReadOnlyDictionary<long, int>> StockAsync(
        this CouponOpsFixture fx, long drawEventId)
    {
        using var scope = fx.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDrawStore>();
        return await store.GetStockAsync(drawEventId, CancellationToken.None);
    }

    public static async Task<int> TicketsAsync(this CouponOpsFixture fx, long drawEventId, string userId)
    {
        using var scope = fx.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDrawStore>();
        return await store.GetTicketsAsync(drawEventId, userId, CancellationToken.None);
    }

    /// <summary>
    /// HTTP 를 거치지 않고 스토어를 직접 호출한다.
    /// 난수를 테스트가 정하므로 결과가 결정적이다 — 분포 검정이 CI 에서 간헐적으로 실패하지 않는다.
    /// </summary>
    public static async Task<DrawOutcome> SpinDirectAsync(
        this CouponOpsFixture fx, long drawEventId, string userId, long randomValue, Guid? requestId = null)
    {
        using var scope = fx.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDrawStore>();
        var cache = scope.ServiceProvider.GetRequiredService<DrawMetaCache>();

        var now = DateTime.UtcNow;
        var meta = await cache.GetAsync(drawEventId, CancellationToken.None)
                   ?? throw new InvalidOperationException($"룰렛 {drawEventId} 메타를 찾을 수 없습니다.");

        return await store.SpinAsync(
            drawEventId, userId, requestId ?? Guid.NewGuid(), now,
            randomValue, DrawDay.For(now, meta.DailyResetAt), logFailure: true, CancellationToken.None);
    }

    /// <summary>보상 우편이 기대 건수만큼 적재될 때까지 기다린다. 적재는 비동기다.</summary>
    public static async Task<List<DrawRewardMail>> WaitForMailsAsync(
        this CouponOpsFixture fx, long drawEventId, string userId, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var scope = fx.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var mails = await db.DrawRewardMails.AsNoTracking()
                .Where(m => m.DrawEventId == drawEventId && m.UserId == userId)
                .ToListAsync();

            if (mails.Count >= expected) return mails;
            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"룰렛 {drawEventId} · {userId} 의 보상 우편 {expected}건이 적재되지 않았습니다.");
    }

    /// <summary>추첨 이력이 기대 건수만큼 적재될 때까지 기다린다. Redis 경로는 비동기 적재다.</summary>
    public static async Task<List<DrawLog>> WaitForDrawLogsAsync(
        this CouponOpsFixture fx, long drawEventId, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var scope = fx.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var logs = await db.DrawLogs.AsNoTracking()
                .Where(l => l.DrawEventId == drawEventId)
                .ToListAsync();

            if (logs.Count >= expected) return logs;
            await Task.Delay(100);
        }

        using var last = fx.CreateScope();
        var lastDb = last.ServiceProvider.GetRequiredService<AppDbContext>();
        var observed = await lastDb.DrawLogs.AsNoTracking()
            .Where(l => l.DrawEventId == drawEventId).CountAsync();

        throw new TimeoutException(
            $"룰렛 {drawEventId} 의 이력 {expected}건이 적재되지 않았습니다 (관측 {observed}건).");
    }
}

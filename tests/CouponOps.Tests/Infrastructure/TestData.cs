using CouponOps.Application;
using CouponOps.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace CouponOps.Tests.Infrastructure;

public static class TestData
{
    /// <summary>바로 발급 가능한(진행중) 이벤트를 만들고 Redis 까지 워밍업한다.</summary>
    public static async Task<CouponEvent> CreateActiveEventAsync(
        this CouponOpsFixture fx, int totalQuantity, int perUserLimit,
        IssuanceMode mode = IssuanceMode.PreGenerated)
    {
        using var scope = fx.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<EventAdminService>();
        var now = DateTime.UtcNow;

        return await provisioning.CreateAndWarmAsync(
            code: $"evt-{Guid.NewGuid():N}"[..20],
            name: "테스트 이벤트",
            startsAtUtc: now.AddMinutes(-1),
            endsAtUtc: now.AddHours(1),
            totalQuantity: totalQuantity,
            perUserLimit: perUserLimit,
            mode: mode,
            ct: CancellationToken.None);
    }

    /// <summary>시작 전/종료 후 등 기간이 어긋난 이벤트.</summary>
    public static async Task<CouponEvent> CreateEventAsync(
        this CouponOpsFixture fx, DateTime startsAtUtc, DateTime endsAtUtc,
        int totalQuantity = 10, int perUserLimit = 1)
    {
        using var scope = fx.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<EventAdminService>();

        return await provisioning.CreateAndWarmAsync(
            $"evt-{Guid.NewGuid():N}"[..20], "테스트 이벤트",
            startsAtUtc, endsAtUtc, totalQuantity, perUserLimit,
            IssuanceMode.PreGenerated, CancellationToken.None);
    }

    public static async Task SuspendAsync(this CouponOpsFixture fx, long eventId, string reason)
    {
        using var scope = fx.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<EventAdminService>();
        await provisioning.SuspendAsync(eventId, reason, CancellationToken.None);
    }

    public static IReadOnlyList<(string, Guid?)> DistinctUsers(int count, string prefix = "user") =>
        Enumerable.Range(0, count).Select(i => ($"{prefix}-{i:D5}", (Guid?)Guid.NewGuid())).ToArray();
}

using System.Diagnostics;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using CouponOps.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace CouponOps.Tests;

/// <summary>
/// Redis 가 source of truth 이고 DB 는 비동기로 따라온다.
/// 여기서 확인하는 것은 "결국 일치하는가(eventual consistency)" 이다.
/// </summary>
[Collection(CouponOpsCollection.Name)]
public sealed class PersistenceTests(CouponOpsFixture fx, ITestOutputHelper output)
{
    private IssueClient Client => new(fx.App);

    [Fact(DisplayName = "발급 이력이 성공·실패 모두 DB 에 비동기로 적재된다")]
    public async Task Issuance_history_is_persisted_asynchronously()
    {
        const int quantity = 30;
        const int requests = 100;

        var ev = await fx.CreateActiveEventAsync(quantity, perUserLimit: 1);
        var started = Stopwatch.GetTimestamp();

        var calls = await Client.IssueConcurrentlyAsync(ev.Id, TestData.DistinctUsers(requests, "persist"));
        var apiDone = Stopwatch.GetElapsedTime(started);

        calls.Count(c => c.Result == nameof(IssueResult.Success)).Should().Be(quantity);

        var logs = await WaitForLogsAsync(ev.Id, requests, TimeSpan.FromSeconds(60));
        var drained = Stopwatch.GetElapsedTime(started);

        output.WriteLine($"API 응답 완료 {apiDone.TotalMilliseconds:F0}ms → DB 적재 완료 {drained.TotalMilliseconds:F0}ms");

        logs.Should().HaveCount(requests, "성공과 실패가 모두 이력에 남아야 한다");
        logs.Count(l => l.Result == IssueResult.Success).Should().Be(quantity);
        logs.Count(l => l.Result == IssueResult.SoldOut).Should().Be(requests - quantity);

        logs.Select(l => l.RequestId).Distinct().Should().HaveCount(requests,
            "at-least-once 소비에도 중복 행이 생기면 안 된다");

        logs.Should().OnlyContain(l => l.IssuePath == "redis-lua");

        // 성공 이력에는 쿠폰이 연결되고, 쿠폰은 발급 상태로 바뀌어야 한다.
        using var scope = fx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var issuedCoupons = await db.Coupons
            .CountAsync(c => c.EventId == ev.Id && c.Status == CouponStatus.Issued);
        issuedCoupons.Should().Be(quantity);

        logs.Where(l => l.Result == IssueResult.Success)
            .Should().OnlyContain(l => l.CouponId != null && l.CouponCode != null);

        // 실패 이력에는 쿠폰이 붙지 않는다.
        logs.Where(l => l.Result != IssueResult.Success)
            .Should().OnlyContain(l => l.CouponId == null);
    }

    [Fact(DisplayName = "적재된 이력의 쿠폰 소유자가 API 응답과 일치한다")]
    public async Task Persisted_owner_matches_the_api_response()
    {
        var ev = await fx.CreateActiveEventAsync(totalQuantity: 20, perUserLimit: 1);
        var calls = await Client.IssueConcurrentlyAsync(ev.Id, TestData.DistinctUsers(20, "owner"));

        var expected = calls.ToDictionary(c => c.Body!.CouponCode!, c => c.Body!.RequestId);

        await WaitForLogsAsync(ev.Id, 20, TimeSpan.FromSeconds(60));

        using var scope = fx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var coupons = await db.Coupons
            .Where(c => c.EventId == ev.Id && c.Status == CouponStatus.Issued)
            .Join(db.IssuanceLogs.Where(l => l.EventId == ev.Id),
                  c => c.Id, l => l.CouponId, (c, l) => new { c.Code, c.IssuedToUserId, l.RequestId, l.UserId })
            .ToListAsync();

        coupons.Should().HaveCount(20);
        coupons.Should().OnlyContain(x => x.IssuedToUserId == x.UserId,
            "쿠폰의 소유자와 이력의 유저가 어긋나면 안 된다");

        foreach (var c in coupons)
            expected[c.Code].Should().Be(c.RequestId.ToString());
    }

    private async Task<List<IssuanceLog>> WaitForLogsAsync(long eventId, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            using var scope = fx.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var logs = await db.IssuanceLogs.AsNoTracking()
                .Where(l => l.EventId == eventId).ToListAsync();

            if (logs.Count >= expected) return logs;

            await Task.Delay(100);
        }

        throw new TimeoutException($"이벤트 {eventId} 의 이력 {expected}건이 {timeout.TotalSeconds}초 안에 적재되지 않았습니다.");
    }
}

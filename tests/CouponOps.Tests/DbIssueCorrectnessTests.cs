using CouponOps.Domain;
using CouponOps.Tests.Infrastructure;
using FluentAssertions;
using Xunit.Abstractions;

namespace CouponOps.Tests;

/// <summary>
/// 4단계 비교 측정의 전제를 확인한다.
/// </summary>
/// <remarks>
/// 빠르지만 초과 발급하는 구현과 비교하면 수치가 아무것도 증명하지 못한다.
/// 세 경로가 <b>모두 정확할 때만</b> 성능 비교가 의미를 가지므로, 여기서 그것을 먼저 단언한다.
/// </remarks>
[Collection(CouponOpsCollection.Name)]
public sealed class DbIssueCorrectnessTests(CouponOpsFixture fx, ITestOutputHelper output)
{
    [Theory(DisplayName = "DB 경로도 초과 발급이 0건이다")]
    [InlineData(IssuePath.DbLock)]
    [InlineData(IssuePath.DbSkipLocked)]
    public async Task Db_paths_do_not_over_issue(IssuePath path)
    {
        const int quantity = 50;
        const int requests = 300;

        var ev = await fx.CreateActiveEventAsync(quantity, perUserLimit: 1);
        var client = new IssueClient(fx.App, path);

        var calls = await client.IssueConcurrentlyAsync(ev.Id, TestData.DistinctUsers(requests, $"{path}"));

        var codes = calls.Where(c => c.Result == nameof(IssueResult.Success))
                         .Select(c => c.Body!.CouponCode!).ToList();

        foreach (var g in calls.GroupBy(c => c.Result).OrderByDescending(g => g.Count()))
            output.WriteLine($"  {path,-14} {g.Key,-14} {g.Count()}");

        codes.Should().HaveCount(quantity, "DB 경로도 설정 수량만큼만 발급해야 한다");
        codes.Distinct().Should().HaveCount(quantity, "한 코드가 두 사람에게 나가면 안 된다");
    }

    [Theory(DisplayName = "DB 경로도 유저당 한도를 지킨다")]
    [InlineData(IssuePath.DbLock)]
    [InlineData(IssuePath.DbSkipLocked)]
    public async Task Db_paths_respect_per_user_limit(IssuePath path)
    {
        const int perUserLimit = 3;
        var ev = await fx.CreateActiveEventAsync(totalQuantity: 100, perUserLimit: perUserLimit);
        var client = new IssueClient(fx.App, path);

        var requests = Enumerable.Range(0, 40)
            .Select(_ => ($"greedy-{path}", (Guid?)Guid.NewGuid()))
            .ToArray();

        var calls = await client.IssueConcurrentlyAsync(ev.Id, requests);
        var success = calls.Count(c => c.Result == nameof(IssueResult.Success));

        output.WriteLine($"  {path}: 동일 유저 40회 동시 → 성공 {success}");
        success.Should().Be(perUserLimit);
    }

    [Theory(DisplayName = "DB 경로도 동일 RequestId 재시도를 멱등 처리한다")]
    [InlineData(IssuePath.DbLock)]
    [InlineData(IssuePath.DbSkipLocked)]
    public async Task Db_paths_are_idempotent(IssuePath path)
    {
        var ev = await fx.CreateActiveEventAsync(totalQuantity: 10, perUserLimit: 1);
        var client = new IssueClient(fx.App, path);
        var requestId = Guid.NewGuid();

        var first = await client.IssueAsync(ev.Id, $"retry-{path}", requestId);
        var second = await client.IssueAsync(ev.Id, $"retry-{path}", requestId);

        first.Result.Should().Be(nameof(IssueResult.Success));
        second.Result.Should().Be(nameof(IssueResult.DuplicateRequest));
        second.Body!.CouponCode.Should().Be(first.Body!.CouponCode);
    }

    [Theory(DisplayName = "DB 경로도 중단된 이벤트를 거부한다")]
    [InlineData(IssuePath.DbLock)]
    [InlineData(IssuePath.DbSkipLocked)]
    public async Task Db_paths_reject_suspended_events(IssuePath path)
    {
        var ev = await fx.CreateActiveEventAsync(totalQuantity: 10, perUserLimit: 1);
        var client = new IssueClient(fx.App, path);

        (await client.IssueAsync(ev.Id, $"before-{path}")).Result.Should().Be(nameof(IssueResult.Success));

        await fx.SuspendAsync(ev.Id, "비교군 중단 테스트");

        (await client.IssueAsync(ev.Id, $"after-{path}")).Result.Should().Be(nameof(IssueResult.Suspended));
    }

    [Fact(DisplayName = "세 경로가 같은 이력 스키마에 IssuePath 만 다르게 기록한다")]
    public async Task All_paths_write_the_same_schema()
    {
        var ev = await fx.CreateActiveEventAsync(totalQuantity: 30, perUserLimit: 1);

        await new IssueClient(fx.App, IssuePath.RedisLua).IssueAsync(ev.Id, "u-redis");
        await new IssueClient(fx.App, IssuePath.DbLock).IssueAsync(ev.Id, "u-dblock");
        await new IssueClient(fx.App, IssuePath.DbSkipLocked).IssueAsync(ev.Id, "u-dbskip");

        var paths = await fx.WaitForIssuePathsAsync(ev.Id, 3, TimeSpan.FromSeconds(60));

        output.WriteLine("  기록된 경로: " + string.Join(", ", paths.Order()));

        paths.Should().BeEquivalentTo(["redis-lua", "db-lock", "db-readpast"],
            "비교 측정이 공정하려면 세 경로가 같은 테이블에 구분 가능하게 남아야 한다");
    }
}

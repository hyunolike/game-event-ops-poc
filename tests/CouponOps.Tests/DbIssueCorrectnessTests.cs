using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

        // ── 안전성: 이것이 깨지면 방식 자체가 틀린 것이다 ──────────────────────
        codes.Count.Should().BeLessThanOrEqualTo(quantity, "초과 발급은 단 한 건도 허용되지 않는다");
        codes.Distinct().Should().HaveCount(codes.Count, "한 코드가 두 사람에게 나가면 안 된다");

        // ── 생존성: 여기는 느슨하게 본다 ───────────────────────────────────────
        // DB 경로는 300개 트랜잭션이 잠금을 두고 경합하므로 교착(1205)이나 타임아웃으로
        // 일부 트랜잭션이 실패할 수 있다. 그것은 이 방식의 특성이지 결함이 아니며,
        // 4단계 측정이 보고하는 지표이기도 하다. (Redis 경로에는 이 완화가 필요 없다 —
        // IssueConcurrencyTests 는 여전히 "정확히 N건" 을 단언한다.)
        //
        // 다만 "쿠폰이 증발하지 않았음" 은 확인해야 한다 — 재고가 남았다면 그만큼
        // 실패한 트랜잭션이 있어야 한다.
        var systemErrors = calls.Count(c => c.Result == nameof(IssueResult.SystemError));
        (codes.Count + systemErrors).Should().BeGreaterThanOrEqualTo(quantity,
            "발급도 안 되고 실패도 아닌 채로 사라진 쿠폰이 있어서는 안 된다");

        // 롤백된 트랜잭션이 쿠폰을 발급 상태로 남겨두면 그 쿠폰은 영영 못 나간다.
        using var scope = fx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var issuedInDb = await db.Coupons
            .CountAsync(c => c.EventId == ev.Id && c.Status == CouponStatus.Issued);

        issuedInDb.Should().Be(codes.Count, "DB 의 발급 상태와 응답이 일치해야 한다");
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

    [Theory(DisplayName = "세 경로가 같은 이력 스키마에 IssuePath 만 다르게 기록한다")]
    [InlineData(IssuePath.RedisLua, "redis-lua")]
    [InlineData(IssuePath.DbLock, "db-lock")]
    [InlineData(IssuePath.DbSkipLocked, "db-readpast")]
    public async Task Each_path_is_recorded_with_its_own_tag(IssuePath path, string expectedTag)
    {
        // 경로마다 별도 이벤트를 쓴다. 한 이벤트에 여러 경로를 섞으면 안 된다 —
        // DB 경로는 Redis 를 건드리지 않으므로, Redis 가 풀에서 꺼내 갔지만 아직 DB 에
        // 반영되지 않은 쿠폰(Status=0)을 DB 경로가 다시 집어간다.
        // 실제 운영에서는 한 이벤트에 한 경로만 쓰므로 생기지 않는 상황이고,
        // 테스트가 그 인위적인 경합을 만들 이유도 없다.
        var ev = await fx.CreateActiveEventAsync(totalQuantity: 10, perUserLimit: 1);

        var call = await new IssueClient(fx.App, path).IssueAsync(ev.Id, $"u-{path}");
        call.Result.Should().Be(nameof(IssueResult.Success));

        var paths = await fx.WaitForIssuePathsAsync(ev.Id, 1, TimeSpan.FromSeconds(60));

        output.WriteLine($"  {path} → {string.Join(", ", paths)}");
        paths.Should().BeEquivalentTo([expectedTag],
            "비교 측정이 공정하려면 경로가 같은 테이블에 구분 가능하게 남아야 한다");
    }
}

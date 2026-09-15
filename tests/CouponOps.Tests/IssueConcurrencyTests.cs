using CouponOps.Domain;
using CouponOps.Infrastructure.Redis;
using CouponOps.Tests.Infrastructure;
using FluentAssertions;
using Xunit.Abstractions;

namespace CouponOps.Tests;

/// <summary>
/// 이 PoC 가 증명해야 할 성질: 동시 요청이 아무리 몰려도 발급 수가 설정 수량을 넘지 않는다.
/// </summary>
[Collection(CouponOpsCollection.Name)]
public sealed class IssueConcurrencyTests(CouponOpsFixture fx, ITestOutputHelper output)
{
    private IssueClient Client => new(fx.App);

    [Fact(DisplayName = "동시 요청 1000건에서 설정 수량 100개만 발급된다")]
    public async Task Exactly_configured_quantity_is_issued_under_1000_concurrent_requests()
    {
        const int quantity = 100;
        const int requests = 1000;

        var ev = await fx.CreateActiveEventAsync(totalQuantity: quantity, perUserLimit: 1);
        var calls = await Client.IssueConcurrentlyAsync(ev.Id, TestData.DistinctUsers(requests));

        var success = calls.Where(c => c.Result == nameof(IssueResult.Success)).ToList();
        var soldOut = calls.Count(c => c.Result == nameof(IssueResult.SoldOut));

        output.WriteLine($"요청 {requests}건 → 성공 {success.Count} / 소진거부 {soldOut}");
        foreach (var g in calls.GroupBy(c => c.Result).OrderByDescending(g => g.Count()))
            output.WriteLine($"  {g.Key,-18} {g.Count()}");

        success.Should().HaveCount(quantity, "설정 수량만큼만 발급돼야 한다");
        soldOut.Should().Be(requests - quantity, "나머지는 전부 소진으로 거부돼야 한다");
        calls.Should().OnlyContain(
            c => c.Result == nameof(IssueResult.Success) || c.Result == nameof(IssueResult.SoldOut),
            "서로 다른 유저의 기간 내 요청이므로 다른 실패 사유가 나올 수 없다");

        // 같은 코드가 두 사람에게 나가지 않았는가
        success.Select(c => c.Body!.CouponCode).Distinct().Should().HaveCount(quantity);

        // Redis 풀이 정확히 비었는가 (음수 재고나 잔여 코드가 없어야 한다)
        var remaining = await fx.Redis.GetDatabase().ListLengthAsync(RedisKeys.Pool(ev.Id));
        remaining.Should().Be(0);
    }

    [Fact(DisplayName = "재고 초과 발급은 0건이다 (2000 요청 / 재고 50)")]
    public async Task Over_issuance_is_exactly_zero()
    {
        const int quantity = 50;
        const int requests = 2000;

        var ev = await fx.CreateActiveEventAsync(totalQuantity: quantity, perUserLimit: 1);
        var calls = await Client.IssueConcurrentlyAsync(ev.Id, TestData.DistinctUsers(requests, "rush"));

        var issuedCodes = calls
            .Where(c => c.Result == nameof(IssueResult.Success))
            .Select(c => c.Body!.CouponCode!)
            .ToList();

        var overIssued = Math.Max(0, issuedCodes.Count - quantity);
        var duplicatedCodes = issuedCodes.Count - issuedCodes.Distinct().Count();

        output.WriteLine($"발급 {issuedCodes.Count} / 재고 {quantity} → 초과발급 {overIssued}, 중복코드 {duplicatedCodes}");

        overIssued.Should().Be(0, "초과 발급은 단 한 건도 허용되지 않는다");
        duplicatedCodes.Should().Be(0, "한 코드가 두 사람에게 나가서는 안 된다");

        // Redis 의 유저별 발급 기록도 정확히 재고만큼이어야 한다.
        // (응답만 보면 앱 계층에서 숫자를 맞춘 것일 수도 있으므로 원천을 직접 확인한다.)
        var issuedUsers = await fx.Redis.GetDatabase().HashLengthAsync(RedisKeys.Users(ev.Id));
        issuedUsers.Should().Be(quantity);
    }

    [Fact(DisplayName = "같은 유저가 동시에 몰아쳐도 유저당 한도만큼만 발급된다")]
    public async Task Per_user_limit_holds_under_concurrency()
    {
        const int perUserLimit = 3;
        var ev = await fx.CreateActiveEventAsync(totalQuantity: 100, perUserLimit: perUserLimit);

        // 한 명이 서로 다른 RequestId 로 50번 동시에 두드린다 (멱등이 아닌 진짜 중복 시도).
        var requests = Enumerable.Range(0, 50)
            .Select(_ => ("greedy-user", (Guid?)Guid.NewGuid()))
            .ToArray();

        var calls = await Client.IssueConcurrentlyAsync(ev.Id, requests);

        var success = calls.Count(c => c.Result == nameof(IssueResult.Success));
        var limited = calls.Count(c => c.Result == nameof(IssueResult.LimitExceeded));

        output.WriteLine($"동일 유저 50회 동시 시도 → 성공 {success} / 한도초과 {limited}");

        success.Should().Be(perUserLimit);
        limited.Should().Be(50 - perUserLimit);
    }

    [Fact(DisplayName = "파생(Derived) 모드에서도 초과 발급이 없다")]
    public async Task Derived_mode_does_not_over_issue()
    {
        const int quantity = 80;
        var ev = await fx.CreateActiveEventAsync(quantity, perUserLimit: 1, mode: IssuanceMode.Derived);

        var calls = await Client.IssueConcurrentlyAsync(ev.Id, TestData.DistinctUsers(800, "derived"));
        var codes = calls.Where(c => c.Result == nameof(IssueResult.Success))
                         .Select(c => c.Body!.CouponCode!).ToList();

        output.WriteLine($"파생 모드 발급 {codes.Count} / 재고 {quantity}, 예시 코드: {codes.FirstOrDefault()}");

        codes.Should().HaveCount(quantity);
        codes.Distinct().Should().HaveCount(quantity, "시퀀스에서 파생하므로 충돌이 없어야 한다");

        var stock = await fx.Redis.GetDatabase().StringGetAsync(RedisKeys.Stock(ev.Id));
        ((int)stock).Should().Be(0, "재고 카운터가 음수로 내려가면 안 된다");
    }
}

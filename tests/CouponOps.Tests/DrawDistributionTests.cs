using CouponOps.Application;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using CouponOps.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CouponOps.Tests;

/// <summary>
/// 실제 분포가 설정한 가중치와 일치하는지 확인한다.
/// </summary>
/// <remarks>
/// 난수를 테스트가 정해 스토어에 직접 넘기므로 <b>결과가 결정적이다</b>.
/// 통계 검정은 본질적으로 확률적으로 실패하는데, 그것이 CI 에 들어오면
/// "가끔 빨개지는 테스트" 가 되어 아무도 믿지 않게 된다. 시드를 고정해 그 여지를 없앤다.
/// (난수를 Lua 밖에서 뽑는 설계 덕분에 가능한 일이다.)
/// </remarks>
[Collection(CouponOpsCollection.Name)]
public sealed class DrawDistributionTests(CouponOpsFixture fx)
{
    /// <summary>자유도 3, 유의수준 0.001 의 카이제곱 임계값.</summary>
    private const double CriticalValue = 16.266;

    [Fact(DisplayName = "당첨 분포가 가중치와 일치한다 (카이제곱 검정)")]
    public async Task Distribution_matches_the_configured_weights()
    {
        const int draws = 4_000;

        // 유저마다 한 번씩만 돌리므로 일일 한도 1 로 충분하다(티켓 불필요).
        var ev = await fx.CreateActiveDrawAsync(
            prizes: DrawTestData.UnlimitedFourPrizes(),
            dailyDrawLimit: 1, ticketItemId: null, ticketCost: 0);

        var slotByPrizeId = await SlotMapAsync(ev.Id);

        var rng = new Random(20260920);
        var observed = new int[4];

        for (var i = 0; i < draws; i++)
        {
            var outcome = await fx.SpinDirectAsync(ev.Id, $"dist-{i}", NextRandom(rng));

            outcome.Result.Should().Be(DrawResult.Won);
            observed[slotByPrizeId[outcome.PrizeId]]++;
        }

        // 가중치 100 : 200 : 300 : 400 → 기대 비율 10% : 20% : 30% : 40%
        double[] expectedRatio = [0.1, 0.2, 0.3, 0.4];

        var chiSquare = 0.0;
        for (var slot = 0; slot < 4; slot++)
        {
            var expected = draws * expectedRatio[slot];
            var diff = observed[slot] - expected;
            chiSquare += diff * diff / expected;
        }

        chiSquare.Should().BeLessThan(CriticalValue,
            $"관측 분포가 가중치와 다릅니다 (χ²={chiSquare:F2}, 관측=[{string.Join(", ", observed)}])");
    }

    [Fact(DisplayName = "난수는 음수가 아니고 48비트를 넘지 않는다")]
    public void Random_values_stay_in_the_lua_safe_range()
    {
        // Lua 5.1 의 수는 double 이라 2^53 을 넘으면 정수 정밀도를 잃는다.
        // 48비트로 자르는 것은 그 경계를 구조적으로 넘지 않기 위해서다.
        for (var i = 0; i < 10_000; i++)
        {
            var value = SpinDrawService.NextRandomValue();
            value.Should().BeGreaterThanOrEqualTo(0);
            value.Should().BeLessThan(1L << 48);
        }
    }

    /// <summary>48비트 난수를 시드로부터 결정적으로 만든다.</summary>
    private static long NextRandom(Random rng) =>
        ((long)rng.Next(1 << 24) << 24) | (uint)rng.Next(1 << 24);

    private async Task<Dictionary<long, int>> SlotMapAsync(long drawEventId)
    {
        using var scope = fx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await db.DrawPrizes.AsNoTracking()
            .Where(p => p.DrawEventId == drawEventId)
            .Select(p => new { p.Id, p.SlotIndex })
            .ToListAsync();

        return rows.ToDictionary(r => r.Id, r => r.SlotIndex);
    }
}

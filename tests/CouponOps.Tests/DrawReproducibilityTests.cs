using CouponOps.Application;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using CouponOps.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CouponOps.Tests;

/// <summary>
/// 이 설계의 핵심 주장을 검증한다 — <b>저장된 이력만으로 추첨을 그대로 재계산할 수 있다.</b>
/// 이것이 성립하지 않으면 CS 클레임·내부 감사·규제 대응에 답할 방법이 없다.
/// </summary>
[Collection(CouponOpsCollection.Name)]
public sealed class DrawReproducibilityTests(CouponOpsFixture fx)
{
    [Fact(DisplayName = "이력의 난수와 확률표로 추첨을 재계산하면 같은 경품이 나온다")]
    public async Task Stored_logs_recompute_to_the_same_prize()
    {
        const int draws = 30;

        var ev = await fx.CreateActiveDrawAsync(
            prizes: DrawTestData.UnlimitedFourPrizes(),
            dailyDrawLimit: 1, ticketItemId: null, ticketCost: 0);

        var rng = new Random(777);
        for (var i = 0; i < draws; i++)
            await fx.SpinDirectAsync(ev.Id, $"verify-{i}", ((long)rng.Next(1 << 24) << 24) | (uint)rng.Next(1 << 24));

        var logs = await fx.WaitForDrawLogsAsync(ev.Id, draws, TimeSpan.FromSeconds(30));

        using var scope = fx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var versions = await db.DrawWeightVersions.AsNoTracking()
            .Where(v => v.DrawEventId == ev.Id).ToListAsync();
        var snapshotById = versions.ToDictionary(v => v.Id, v => DrawSnapshot.Deserialize(v.SnapshotJson));

        logs.Should().HaveCount(draws);

        foreach (var log in logs)
        {
            log.WeightVersionId.Should().NotBeNull("확률표를 가리키지 않는 이력은 설명할 수 없다");

            var recomputed = DrawSnapshot.Recompute(
                snapshotById[log.WeightVersionId!.Value], log.RandomValue, log.PityApplied, log.PrizeId);

            // 대체가 적용된 이력은 치환 전 슬롯과 비교한다 —
            // 치환은 당시 재고에 의존하므로 순수 함수로 재현되지 않는다.
            var expected = log.FallbackApplied ? log.OriginalPrizeId : log.PrizeId;
            recomputed.Should().Be(expected,
                $"이력 {log.Id} 재계산 불일치 (random={log.RandomValue}, roll={log.Roll})");
        }
    }

    [Fact(DisplayName = "확률 변경은 새 버전을 만들고, 이전 이력은 이전 확률표를 계속 가리킨다")]
    public async Task Changing_weights_creates_a_new_version_and_keeps_history_explainable()
    {
        var ev = await fx.CreateActiveDrawAsync(
            prizes: DrawTestData.UnlimitedFourPrizes(),
            dailyDrawLimit: 1, ticketItemId: null, ticketCost: 0);

        await fx.SpinDirectAsync(ev.Id, "before-change", 12_345_678);
        var beforeLogs = await fx.WaitForDrawLogsAsync(ev.Id, 1, TimeSpan.FromSeconds(30));
        var versionBefore = beforeLogs.Single().WeightVersionId;

        using (var scope = fx.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<DrawAdminService>();
            await admin.ChangeWeightsAsync(
                ev.Id,
                new Dictionary<int, int> { [0] = 500, [1] = 200, [2] = 200, [3] = 100 },
                reason: "1등 체감 개선",
                CancellationToken.None);
        }

        await fx.SpinDirectAsync(ev.Id, "after-change", 12_345_678);
        var logs = await fx.WaitForDrawLogsAsync(ev.Id, 2, TimeSpan.FromSeconds(30));

        using var check = fx.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();

        var versions = await db.DrawWeightVersions.AsNoTracking()
            .Where(v => v.DrawEventId == ev.Id).OrderBy(v => v.Version).ToListAsync();

        versions.Should().HaveCount(2, "확률 변경은 UPDATE 가 아니라 새 버전이어야 한다");
        versions[0].DeactivatedAt.Should().NotBeNull("이전 버전은 비활성화된다");
        versions[1].DeactivatedAt.Should().BeNull();
        versions[0].SnapshotHash.Should().NotBe(versions[1].SnapshotHash);
        versions[1].ChangeReason.Should().Be("1등 체감 개선");

        var after = logs.Single(l => l.UserId == "after-change");
        after.WeightVersionId.Should().Be(versions[1].Id);
        logs.Single(l => l.UserId == "before-change").WeightVersionId.Should().Be(versionBefore,
            "과거 이력이 새 확률표를 가리키면, 그 추첨을 더 이상 설명할 수 없다");

        // 같은 난수인데 확률표가 달라졌으므로 결과도 달라져야 한다 — 변경이 실제로 적용됐다는 증거다.
        var snapshotV1 = DrawSnapshot.Deserialize(versions[0].SnapshotJson);
        var snapshotV2 = DrawSnapshot.Deserialize(versions[1].SnapshotJson);
        DrawSnapshot.Recompute(snapshotV1, 12_345_678, false, null)
            .Should().NotBe(DrawSnapshot.Recompute(snapshotV2, 12_345_678, false, null));
    }

    [Fact(DisplayName = "확률 변경에는 사유가 필수다")]
    public async Task Weight_change_requires_a_reason()
    {
        var ev = await fx.CreateActiveDrawAsync(
            prizes: DrawTestData.UnlimitedFourPrizes(),
            dailyDrawLimit: 1, ticketItemId: null, ticketCost: 0);

        using var scope = fx.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<DrawAdminService>();

        var act = () => admin.ChangeWeightsAsync(
            ev.Id, new Dictionary<int, int> { [0] = 500 }, reason: "  ", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}

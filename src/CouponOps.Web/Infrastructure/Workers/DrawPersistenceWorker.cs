using CouponOps.Application;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using CouponOps.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CouponOps.Infrastructure.Workers;

/// <summary>
/// Redis 스트림에 쌓인 추첨 이력을 DB 로 옮기고, 당첨분을 우편함에 넣는다.
/// </summary>
/// <remarks>
/// Redis 가 판정의 원천이고 DB 는 영속화 담당이다. 이 워커가 늦거나 잠시 멈춰도
/// 추첨의 정확성(재고를 넘긴 지급이 없다)에는 영향이 없다 — 영향받는 것은
/// 운영툴에 보이는 이력의 신선도와 우편 도착 시각뿐이다.
/// <para>
/// 소비는 at-least-once 이므로 같은 항목이 두 번 올 수 있다.
/// <c>DrawLogs.RequestId</c> 와 <c>DrawRewardMails.RequestId</c> 의 UNIQUE 제약,
/// 그리고 이 클래스의 선필터가 중복 행을 막는다.
/// </para>
/// </remarks>
public sealed class DrawPersistenceWorker(
    IServiceScopeFactory scopes,
    IDrawStore store,
    DrawMetaCache metaCache,
    IOptions<DrawOptions> options,
    ILogger<DrawPersistenceWorker> log) : BackgroundService
{
    private readonly DrawOptions _opts = options.Value;
    private readonly string _consumer = $"{Environment.MachineName}:{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // 재기동 직후 한 번은 자기 PEL 을 회수한다. 이전 프로세스가 적재 도중 죽었다면
        // 그 항목들은 '>' 로 다시 오지 않는다.
        var reclaim = true;

        while (!ct.IsCancellationRequested)
        {
            var moved = 0;
            try
            {
                foreach (var eventId in await store.GetRegisteredEventIdsAsync(ct))
                    moved += await DrainAsync(eventId, reclaim, ct);

                reclaim = false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 한 주기의 실패로 워커를 죽이지 않는다. 확인(ACK)하지 않았으므로 항목은 남아 있고
                // 다음 주기에 다시 시도된다.
                log.LogError(ex, "추첨 이력 적재 주기 실패");
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }

            if (moved == 0) await Task.Delay(_opts.PersistencePollInterval, ct);
        }
    }

    private async Task<int> DrainAsync(long drawEventId, bool reclaim, CancellationToken ct)
    {
        var entries = await store.ReadPendingAsync(drawEventId, _consumer, _opts.PersistenceBatchSize, reclaim, ct);
        if (entries.Count == 0) return 0;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await PersistAsync(db, drawEventId, entries, ct);
        await store.AcknowledgeAsync(drawEventId, entries.Select(e => e.StreamId).ToArray(), ct);

        return entries.Count;
    }

    private async Task PersistAsync(
        AppDbContext db, long drawEventId, IReadOnlyList<DrawnEntry> entries, CancellationToken ct)
    {
        // 이미 적재된 요청을 걸러낸다. UNIQUE 제약에 맡기고 예외로 처리하면
        // 재처리 배치 전체가 실패한다.
        var requestIds = entries.Select(e => e.RequestId).ToList();
        var already = (await db.DrawLogs
                .Where(l => requestIds.Contains(l.RequestId))
                .Select(l => l.RequestId)
                .ToListAsync(ct))
            .ToHashSet();

        var fresh = entries.Where(e => !already.Contains(e.RequestId)).ToList();
        if (fresh.Count == 0) return;

        var meta = await metaCache.GetAsync(drawEventId, ct);
        var now = DateTime.UtcNow;

        // 이력과 우편은 한 트랜잭션이어야 한다. 우편만 들어가고 이력이 빠지면
        // 재처리 시 선필터가 걸러주지 못해 같은 보상이 두 번 발송된다.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        foreach (var e in fresh)
        {
            var prizeMeta = meta is not null && meta.Prizes.TryGetValue(e.PrizeId, out var pm) ? pm : null;

            db.DrawLogs.Add(DrawLog.Create(
                e.RequestId, drawEventId, e.UserId, e.Result,
                weightVersionId: e.WeightVersionId > 0 ? e.WeightVersionId : null,
                randomValue: e.RandomValue, totalWeight: e.TotalWeight, roll: e.Roll,
                prizeId: e.PrizeId > 0 ? e.PrizeId : null,
                originalPrizeId: e.OriginalPrizeId > 0 ? e.OriginalPrizeId : null,
                fallbackApplied: e.FallbackApplied, pityApplied: e.PityApplied,
                prizeName: prizeMeta?.Name,
                itemId: e.ItemId > 0 ? e.ItemId : null, itemQty: e.ItemQty,
                ticketsSpent: e.TicketsSpent, pityCountAfter: e.PityCountAfter,
                requestedAtUtc: e.RequestedAtUtc, persistedAtUtc: now, clientIp: e.ClientIp));

            // 꽝은 우편을 만들지 않는다 — 보낼 것이 없다.
            // 경품 메타를 모르면(캐시 미스·삭제된 경품) 우편을 만들지 않고 경고만 남긴다.
            // 추첨 자체는 이미 확정이므로 이력은 반드시 남기고, 우편은 재처리 대상이다.
            if (e.Result != DrawResult.Won || e.PrizeId <= 0) continue;
            if (prizeMeta is null)
            {
                log.LogWarning("경품 {PrizeId} 메타를 찾을 수 없어 우편을 만들지 않았습니다 (draw {EventId})",
                    e.PrizeId, drawEventId);
                continue;
            }
            if (prizeMeta.IsBlank) continue;

            db.DrawRewardMails.Add(DrawRewardMail.Create(
                e.RequestId, drawEventId, e.UserId,
                e.PrizeId, prizeMeta.Name, e.ItemId, e.ItemQty, now));
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}

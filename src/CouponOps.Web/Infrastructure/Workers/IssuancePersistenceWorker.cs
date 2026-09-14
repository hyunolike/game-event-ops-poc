using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using CouponOps.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CouponOps.Infrastructure.Workers;

/// <summary>
/// Redis 스트림에 쌓인 발급 이력을 DB 로 옮긴다.
/// </summary>
/// <remarks>
/// Redis 가 source of truth 이고 DB 는 영속화 담당이다. 따라서 이 워커가 늦거나 잠시 멈춰도
/// 발급의 정확성(정확히 N개)에는 영향이 없다 — 영향받는 것은 운영툴에 보이는 이력의 신선도뿐이다.
/// 소비는 at-least-once 이므로 같은 항목이 두 번 올 수 있고, IssuanceLogs.RequestId 의
/// UNIQUE 제약과 이 클래스의 선필터가 중복 행을 막는다.
/// </remarks>
public sealed class IssuancePersistenceWorker(
    IServiceScopeFactory scopes,
    IIssuanceStore store,
    IOptions<IssuanceOptions> options,
    ILogger<IssuancePersistenceWorker> log) : BackgroundService
{
    private readonly IssuanceOptions _opts = options.Value;
    private readonly string _consumer = $"{Environment.MachineName}:{Environment.ProcessId}";
    private readonly Dictionary<long, IssuanceMode> _modeCache = new();

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
                log.LogError(ex, "발급 이력 적재 주기 실패");
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }

            // 처리할 것이 있었다면 쉬지 않고 계속 비운다. 스파이크 직후 밀린 이력을 빨리 따라잡는다.
            if (moved == 0) await Task.Delay(_opts.PersistencePollInterval, ct);
        }
    }

    private async Task<int> DrainAsync(long eventId, bool reclaim, CancellationToken ct)
    {
        var entries = await store.ReadPendingAsync(eventId, _consumer, _opts.PersistenceBatchSize, reclaim, ct);
        if (entries.Count == 0) return 0;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await PersistAsync(db, eventId, entries, ct);
        await store.AcknowledgeAsync(eventId, entries.Select(e => e.StreamId).ToArray(), ct);

        return entries.Count;
    }

    private async Task PersistAsync(
        AppDbContext db, long eventId, IReadOnlyList<IssuedEntry> entries, CancellationToken ct)
    {
        var mode = await GetModeAsync(db, eventId, ct);

        // 이미 적재된 요청을 걸러낸다. UNIQUE 제약에 맡기고 예외로 처리하면
        // 재처리 배치 전체가 실패한다.
        var requestIds = entries.Select(e => e.RequestId).ToList();
        var already = (await db.IssuanceLogs
                .Where(l => requestIds.Contains(l.RequestId))
                .Select(l => l.RequestId)
                .ToListAsync(ct))
            .ToHashSet();

        var fresh = entries.Where(e => !already.Contains(e.RequestId)).ToList();
        if (fresh.Count == 0) return;

        // 쿠폰 반영과 이력 적재는 한 트랜잭션이어야 한다. 쿠폰만 반영되고 이력이 빠지면
        // 재처리 시 선필터가 걸러주지 못해 같은 쿠폰이 두 번 처리된다.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var now = DateTime.UtcNow;
        var success = fresh.Where(e => e.Result == IssueResult.Success && e.CouponCode is not null).ToList();
        var couponIdByCode = new Dictionary<string, long>(StringComparer.Ordinal);

        if (success.Count > 0)
        {
            var codes = success.Select(e => e.CouponCode!).ToList();

            if (mode == IssuanceMode.PreGenerated)
            {
                // 코드는 이미 Coupons 에 있다. 발급 상태로 바꾸기만 한다.
                var coupons = await db.Coupons.Where(c => codes.Contains(c.Code)).ToListAsync(ct);
                var byCode = coupons.ToDictionary(c => c.Code, StringComparer.Ordinal);

                foreach (var entry in success)
                {
                    if (!byCode.TryGetValue(entry.CouponCode!, out var coupon))
                    {
                        // Redis 풀에는 있었으나 DB 에 없는 코드. 워밍업과 DB 적재가 어긋난 경우로,
                        // 발급 자체는 이미 확정이므로 이력은 남기고 경고만 올린다.
                        log.LogWarning("쿠폰 코드 {Code} 가 DB 에 없습니다 (event {EventId})", entry.CouponCode, eventId);
                        continue;
                    }
                    coupon.TryMarkIssued(entry.UserId, entry.RequestedAtUtc);
                    couponIdByCode[coupon.Code] = coupon.Id;
                }
            }
            else
            {
                // 파생 모드는 발급 시점에 코드가 생기므로 여기서 행을 만든다.
                var created = success
                    .Select(e => Coupon.CreateIssued(eventId, e.CouponCode!, e.UserId, e.RequestedAtUtc))
                    .ToList();

                db.Coupons.AddRange(created);
                await db.SaveChangesAsync(ct);   // Id 확보

                foreach (var c in created) couponIdByCode[c.Code] = c.Id;
            }
        }

        foreach (var entry in fresh)
        {
            var logRow = IssuanceLog.Create(
                entry.RequestId, eventId, entry.UserId, entry.Result,
                entry.CouponCode, entry.RequestedAtUtc, now, issuePath: "redis-lua");

            if (entry.CouponCode is not null && couponIdByCode.TryGetValue(entry.CouponCode, out var cid))
                logRow.AttachCoupon(cid);

            db.IssuanceLogs.Add(logRow);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task<IssuanceMode> GetModeAsync(AppDbContext db, long eventId, CancellationToken ct)
    {
        if (_modeCache.TryGetValue(eventId, out var cached)) return cached;

        var mode = await db.Events.Where(e => e.Id == eventId)
            .Select(e => e.IssuanceMode).SingleAsync(ct);

        return _modeCache[eventId] = mode;
    }
}

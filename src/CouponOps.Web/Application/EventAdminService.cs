using System.Data;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using CouponOps.Infrastructure.Redis;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CouponOps.Application;

/// <summary>운영자가 이벤트에 가하는 모든 변경. 변경과 감사 로그가 한 트랜잭션에서 커밋된다.</summary>
public sealed class EventAdminService(
    AppDbContext db, IIssuanceStore store, IAuditLogger audit, TimeProvider clock)
{
    private const string TargetType = nameof(CouponEvent);

    public async Task<CouponEvent> CreateAndWarmAsync(
        string code, string name, DateTime startsAtUtc, DateTime endsAtUtc,
        int totalQuantity, int perUserLimit, IssuanceMode mode, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var ev = CouponEvent.Create(code, name, startsAtUtc, endsAtUtc, totalQuantity, perUserLimit, mode, now);

        db.Events.Add(ev);
        await db.SaveChangesAsync(ct);   // Id 확보

        audit.Record(OperationAction.EventCreated, TargetType, ev.Id.ToString(),
            before: null, after: EventSnapshot.Of(ev));

        string[] codes = [];
        if (mode == IssuanceMode.PreGenerated)
        {
            codes = CouponCodeGenerator.Generate(totalQuantity);
            await BulkInsertCouponsAsync(ev.Id, codes, ct);
        }

        await store.WarmAsync(ev, codes, ct);

        ev.MarkPoolWarmed(clock.GetUtcNow().UtcDateTime);
        audit.Record(OperationAction.CouponPoolWarmed, TargetType, ev.Id.ToString(),
            before: null, after: new { ev.TotalQuantity, Mode = mode.ToString() });

        await db.SaveChangesAsync(ct);
        return ev;
    }

    /// <summary>
    /// 이벤트 수정.
    /// </summary>
    /// <remarks>
    /// 수량 변경에는 제약을 둔다.
    /// <list type="bullet">
    /// <item>시작 전(Scheduled)이면 자유롭게 바꾼다 — 아직 아무도 발급받지 않았으므로 전체 재워밍업이 안전하다.</item>
    /// <item>시작 후에는 <b>증량만</b> 허용하고 기존 풀 뒤에 덧붙인다.</item>
    /// <item>시작 후 감량은 거부한다. 이미 나간 쿠폰을 되돌릴 수 없어 "남은 재고" 의 의미가 무너지고,
    ///       Redis 풀을 잘라내는 순간 누가 못 받게 되는지 예측할 수 없다.
    ///       정말 멈춰야 한다면 강제 중단이 올바른 수단이다.</item>
    /// </list>
    /// </remarks>
    public async Task<CouponEvent> UpdateAsync(
        long eventId, string name, DateTime startsAtUtc, DateTime endsAtUtc,
        int totalQuantity, int perUserLimit, string? reason, CancellationToken ct)
    {
        var ev = await db.Events.SingleAsync(e => e.Id == eventId, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var before = EventSnapshot.Of(ev);

        var isScheduled = ev.StatusAt(now) == EventStatus.Scheduled;
        var delta = totalQuantity - ev.TotalQuantity;

        if (!isScheduled && delta < 0)
            throw new InvalidOperationException(
                "이미 시작된 이벤트의 수량은 줄일 수 없습니다. 발급을 멈추려면 강제 중단을 사용하세요.");

        ev.Update(name, startsAtUtc, endsAtUtc, totalQuantity, perUserLimit, now);

        audit.Record(OperationAction.EventUpdated, TargetType, ev.Id.ToString(), before, EventSnapshot.Of(ev), reason);

        string[] newCodes = [];
        if (delta > 0 && ev.IssuanceMode == IssuanceMode.PreGenerated)
        {
            newCodes = CouponCodeGenerator.Generate(delta);
            await BulkInsertCouponsAsync(ev.Id, newCodes, ct);
        }

        await db.SaveChangesAsync(ct);

        // DB 커밋 이후에 Redis 를 맞춘다. 반대 순서면 DB 저장 실패 시 Redis 에만 반영된
        // 유령 상태가 남는다(Redis 가 source of truth 이므로 그쪽이 더 위험하다).
        if (isScheduled && delta != 0)
        {
            // 시작 전이므로 풀을 통째로 다시 만든다.
            var allCodes = ev.IssuanceMode == IssuanceMode.PreGenerated
                ? await db.Coupons.Where(c => c.EventId == ev.Id && c.Status == CouponStatus.Unissued)
                                  .Select(c => c.Code).ToListAsync(ct)
                : [];
            await store.WarmAsync(ev, allCodes, ct);
        }
        else
        {
            await store.RefreshMetaAsync(ev, ct);
            if (delta > 0) await store.AppendStockAsync(ev, delta, newCodes, ct);
        }

        return ev;
    }

    public async Task SuspendAsync(long eventId, string reason, CancellationToken ct)
    {
        var ev = await db.Events.SingleAsync(e => e.Id == eventId, ct);
        var before = EventSnapshot.Of(ev);

        ev.Suspend(clock.GetUtcNow().UtcDateTime, reason);
        audit.Record(OperationAction.EventSuspended, TargetType, ev.Id.ToString(),
            before, EventSnapshot.Of(ev), reason);

        await db.SaveChangesAsync(ct);

        // 중단은 즉시 효력이 있어야 한다. DB 커밋 직후 바로 Redis 에 반영한다.
        await store.SetSuspendedAsync(eventId, true, ct);
    }

    public async Task ResumeAsync(long eventId, string? reason, CancellationToken ct)
    {
        var ev = await db.Events.SingleAsync(e => e.Id == eventId, ct);
        var before = EventSnapshot.Of(ev);

        ev.Resume(clock.GetUtcNow().UtcDateTime);
        audit.Record(OperationAction.EventResumed, TargetType, ev.Id.ToString(),
            before, EventSnapshot.Of(ev), reason);

        await db.SaveChangesAsync(ct);
        await store.SetSuspendedAsync(eventId, false, ct);
    }

    /// <summary>
    /// 미발급 쿠폰을 SqlBulkCopy 로 적재한다.
    /// EF Core 의 AddRange + SaveChanges 는 배치당 왕복이 생겨 수십만 건에서 수 분이 걸린다.
    /// </summary>
    private async Task BulkInsertCouponsAsync(long eventId, IReadOnlyList<string> codes, CancellationToken ct)
    {
        var table = new DataTable();
        table.Columns.Add("EventId", typeof(long));
        table.Columns.Add("Code", typeof(string));
        table.Columns.Add("Status", typeof(byte));

        foreach (var code in codes)
            table.Rows.Add(eventId, code, (byte)CouponStatus.Unissued);

        await using var conn = new SqlConnection(db.Database.GetConnectionString());
        await conn.OpenAsync(ct);

        using var bulk = new SqlBulkCopy(conn) { DestinationTableName = "Coupons", BatchSize = 5000 };
        bulk.ColumnMappings.Add("EventId", "EventId");
        bulk.ColumnMappings.Add("Code", "Code");
        bulk.ColumnMappings.Add("Status", "Status");
        await bulk.WriteToServerAsync(table, ct);
    }
}

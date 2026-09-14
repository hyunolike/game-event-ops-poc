using System.Data;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using CouponOps.Infrastructure.Redis;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CouponOps.Application;

/// <summary>
/// 이벤트를 만들고 오픈 가능한 상태로 준비한다(코드 사전 생성 + Redis 워밍업).
/// </summary>
public sealed class EventProvisioningService(
    AppDbContext db, IIssuanceStore store, TimeProvider clock)
{
    public async Task<CouponEvent> CreateAndWarmAsync(
        string code, string name, DateTime startsAtUtc, DateTime endsAtUtc,
        int totalQuantity, int perUserLimit, IssuanceMode mode, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var ev = CouponEvent.Create(code, name, startsAtUtc, endsAtUtc, totalQuantity, perUserLimit, mode, now);

        db.Events.Add(ev);
        await db.SaveChangesAsync(ct);

        string[] codes = [];
        if (mode == IssuanceMode.PreGenerated)
        {
            codes = CouponCodeGenerator.Generate(totalQuantity);
            await BulkInsertCouponsAsync(ev.Id, codes, ct);
        }

        await store.WarmAsync(ev, codes, ct);

        ev.MarkPoolWarmed(clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);

        return ev;
    }

    /// <summary>
    /// 미발급 쿠폰을 SqlBulkCopy 로 적재한다.
    /// EF Core 의 AddRange + SaveChanges 는 배치당 왕복이 생겨 수십만 건에서 수 분이 걸린다.
    /// 사전 생성은 이벤트 오픈 전 1회이지만, 운영자를 몇 분씩 기다리게 할 이유는 없다.
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

    /// <summary>운영자 강제 중단. DB 와 Redis 메타를 함께 바꾼다.</summary>
    public async Task SuspendAsync(long eventId, string reason, CancellationToken ct)
    {
        var ev = await db.Events.SingleAsync(e => e.Id == eventId, ct);
        ev.Suspend(clock.GetUtcNow().UtcDateTime, reason);
        await db.SaveChangesAsync(ct);

        // Redis 를 나중에 반영하면 그 사이 발급이 계속된다. 중단은 즉시 효력이 있어야 하므로
        // DB 커밋 직후 바로 반영한다. (반대 순서로 두면 DB 저장 실패 시 이벤트가 유령 중단된다.)
        await store.SetSuspendedAsync(eventId, true, ct);
    }

    public async Task ResumeAsync(long eventId, CancellationToken ct)
    {
        var ev = await db.Events.SingleAsync(e => e.Id == eventId, ct);
        ev.Resume(clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        await store.SetSuspendedAsync(eventId, false, ct);
    }
}

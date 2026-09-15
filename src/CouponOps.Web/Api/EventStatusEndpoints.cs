using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using CouponOps.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;

namespace CouponOps.Api;

public sealed record EventStatusResponse(
    long EventId, string Status, int TotalQuantity, long Remaining, long Issued,
    double ConsumedRate, long PersistedLogs, long PendingPersistence, string ServerTimeUtc);

/// <summary>
/// 이벤트 상세 화면이 폴링하는 현황 API.
/// </summary>
/// <remarks>
/// WebSocket 을 쓰지 않는다. 화면을 보는 사람은 운영자 몇 명이고 갱신 주기는 초 단위면 충분하다.
/// 연결 상태 관리·재연결·스케일아웃 시 백플레인 같은 비용을 치를 이유가 없다.
/// </remarks>
public static class EventStatusEndpoints
{
    public static IEndpointRouteBuilder MapEventStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/events/{eventId:long}/status", GetStatusAsync)
           .RequireAuthorization()
           .WithName("EventStatus");

        return app;
    }

    private static async Task<IResult> GetStatusAsync(
        long eventId, AppDbContext db, IIssuanceStore store, TimeProvider clock, CancellationToken ct)
    {
        var ev = await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.Id == eventId, ct);
        if (ev is null) return Results.NotFound();

        var now = clock.GetUtcNow().UtcDateTime;
        var remaining = await store.GetRemainingAsync(eventId, ev.IssuanceMode, ct);
        var issued = Math.Max(0, ev.TotalQuantity - remaining);

        // DB 는 Redis 를 비동기로 따라온다. 둘의 차이가 곧 적재 지연이므로 화면에 같이 보여준다 —
        // "발급은 됐는데 이력이 아직 안 보인다" 는 문의를 만들지 않기 위함이다.
        var persisted = await db.IssuanceLogs
            .CountAsync(l => l.EventId == eventId && l.Result == IssueResult.Success, ct);

        return Results.Ok(new EventStatusResponse(
            EventId: eventId,
            Status: ev.StatusAt(now).ToString(),
            TotalQuantity: ev.TotalQuantity,
            Remaining: remaining,
            Issued: issued,
            ConsumedRate: ev.TotalQuantity == 0 ? 0 : Math.Round(issued * 100.0 / ev.TotalQuantity, 1),
            PersistedLogs: persisted,
            PendingPersistence: Math.Max(0, issued - persisted),
            ServerTimeUtc: now.ToString("O")));
    }
}

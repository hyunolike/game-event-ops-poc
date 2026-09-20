using CouponOps.Application;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using CouponOps.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;

namespace CouponOps.Api;

public sealed record DrawStatusRow(
    int SlotIndex, string Name, string Percent, long Observed, decimal Expected,
    string Deviation, string Stock, bool IsJackpot);

public sealed record DrawStatusResponse(
    long DrawEventId, string Status, long TotalDraws,
    IReadOnlyList<DrawStatusRow> Rows,
    double? ChiSquare, double Critical, bool Alarming,
    long PersistedLogs, long PendingPersistence, string ServerTimeUtc);

/// <summary>
/// 룰렛 상세 화면이 폴링하는 현황 API.
/// </summary>
/// <remarks>
/// 쿠폰 현황 API 와 같은 판단으로 WebSocket 을 쓰지 않는다 —
/// 보는 사람은 운영자 몇 명이고, 초 단위 갱신이면 의사결정에 충분하다.
/// </remarks>
public static class DrawStatusEndpoints
{
    public static IEndpointRouteBuilder MapDrawStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/draws/{drawEventId:long}/status", GetStatusAsync)
           .RequireAuthorization()
           .WithName("DrawStatus");

        return app;
    }

    private static async Task<IResult> GetStatusAsync(
        long drawEventId, AppDbContext db, IDrawStore store, TimeProvider clock, CancellationToken ct)
    {
        var ev = await db.DrawEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Id == drawEventId, ct);
        if (ev is null) return Results.NotFound();

        var prizes = await db.DrawPrizes.AsNoTracking()
            .Where(p => p.DrawEventId == drawEventId).ToListAsync(ct);

        var wins = await store.GetWinCountsAsync(drawEventId, ct);
        var stock = await store.GetStockAsync(drawEventId, ct);
        var report = DrawDeviation.Compute(prizes, wins, stock);

        // DB 는 Redis 를 비동기로 따라온다. 둘의 차이가 곧 적재 지연이다.
        var persisted = await db.DrawLogs
            .CountAsync(l => l.DrawEventId == drawEventId && l.Result == DrawResult.Won, ct);

        var now = clock.GetUtcNow().UtcDateTime;

        return Results.Ok(new DrawStatusResponse(
            DrawEventId: drawEventId,
            Status: ev.StatusAt(now).ToString(),
            TotalDraws: report.TotalDraws,
            Rows: report.Rows.Select(r => new DrawStatusRow(
                r.SlotIndex, r.Name, $"{r.Percent:F2}%", r.Observed, r.Expected,
                r.Deviation is null ? "-" : $"{r.Deviation:+0.0;-0.0;0}%",
                r.Stock < 0 ? "∞" : r.Stock.ToString("N0"),
                r.IsJackpot)).ToList(),
            ChiSquare: report.ChiSquare,
            Critical: report.Critical,
            Alarming: report.Alarming,
            PersistedLogs: persisted,
            PendingPersistence: Math.Max(0, report.TotalDraws - persisted),
            ServerTimeUtc: now.ToString("O")));
    }
}

using CouponOps.Application;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using CouponOps.Infrastructure.Redis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CouponOps.Pages.Draws;

public sealed record DrawRow(
    DrawEvent Event, EventStatus Status, int PrizeCount, int JackpotStock, int JackpotRemaining, long TotalDraws);

public sealed class IndexModel(
    AppDbContext db, IDrawStore store, TimeProvider clock) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? Keyword { get; set; }

    public List<DrawRow> Rows { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var query = db.DrawEvents.AsNoTracking();

        // 상태 필터는 SQL 술어로 번역된다 — 전건을 읽어 메모리에서 거르지 않는다.
        if (Enum.TryParse<EventStatus>(Status, out var status))
            query = query.Where(DrawEvent.IsStatus(status, now));

        if (!string.IsNullOrWhiteSpace(Keyword))
            query = query.Where(e => e.Name.Contains(Keyword) || e.Code.Contains(Keyword));

        var events = await query.OrderByDescending(e => e.StartsAt).Take(100).ToListAsync(ct);
        var ids = events.Select(e => e.Id).ToList();

        var prizes = await db.DrawPrizes.AsNoTracking()
            .Where(p => ids.Contains(p.DrawEventId))
            .ToListAsync(ct);

        foreach (var ev in events)
        {
            var mine = prizes.Where(p => p.DrawEventId == ev.Id).ToList();

            // 한정 경품(잭팟 우선)의 소진 상황이 목록에서 가장 중요한 정보다 —
            // 운영자가 "1등이 나갔는지" 를 보려고 이 화면을 연다.
            var headline = mine.FirstOrDefault(p => p.IsJackpot) ?? mine.FirstOrDefault(p => !p.IsUnlimited);

            var stock = await store.GetStockAsync(ev.Id, ct);
            var wins = await store.GetWinCountsAsync(ev.Id, ct);

            Rows.Add(new DrawRow(
                ev, ev.StatusAt(now), mine.Count,
                JackpotStock: headline?.InitialStock ?? -1,
                JackpotRemaining: headline is null ? -1 : stock.GetValueOrDefault(headline.Id, headline.InitialStock),
                TotalDraws: wins.Values.Sum()));
        }
    }
}

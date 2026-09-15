using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using CouponOps.Infrastructure.Redis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CouponOps.Pages.Events;

public sealed class IndexModel(AppDbContext db, IIssuanceStore store, TimeProvider clock) : PageModel
{
    public sealed record Row(CouponEvent Event, EventStatus Status, long Remaining, long Issued, double Rate);

    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? Keyword { get; set; }

    public List<Row> Rows { get; private set; } = [];
    public DateTime Now { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Now = clock.GetUtcNow().UtcDateTime;

        var query = db.Events.AsNoTracking();

        // 상태 필터는 SQL 로 내려간다 (CouponEvent.IsStatus). 목록이 커져도 전건을 읽지 않는다.
        if (Enum.TryParse<EventStatus>(Status, out var status))
            query = query.Where(CouponEvent.IsStatus(status, Now));

        if (!string.IsNullOrWhiteSpace(Keyword))
            query = query.Where(e => e.Name.Contains(Keyword) || e.Code.Contains(Keyword));

        var events = await query.OrderByDescending(e => e.StartsAt).Take(200).ToListAsync(ct);

        // 잔여 수량은 Redis 가 원천이다. 이벤트 수가 화면당 수십 건이라 건별 조회로 충분하다.
        // 수백 건 규모가 되면 파이프라인으로 묶어야 한다.
        foreach (var e in events)
        {
            var remaining = await store.GetRemainingAsync(e.Id, e.IssuanceMode, ct);
            var issued = Math.Max(0, e.TotalQuantity - remaining);
            var rate = e.TotalQuantity == 0 ? 0 : Math.Round(issued * 100.0 / e.TotalQuantity, 1);
            Rows.Add(new Row(e, e.StatusAt(Now), remaining, issued, rate));
        }
    }
}

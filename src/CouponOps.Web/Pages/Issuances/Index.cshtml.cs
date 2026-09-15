using CouponOps.Application;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CouponOps.Pages.Issuances;

public sealed class IndexModel(AppDbContext db) : PageModel
{
    public const int PageSize = 50;

    [BindProperty(SupportsGet = true)] public long? EventId { get; set; }
    [BindProperty(SupportsGet = true)] public string? UserId { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }
    [BindProperty(SupportsGet = true)] public string? Result { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public List<IssuanceLog> Rows { get; private set; } = [];
    public List<(long Id, string Name)> EventOptions { get; private set; } = [];
    public int TotalCount { get; private set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));

    public async Task OnGetAsync(CancellationToken ct)
    {
        P = Math.Max(1, P);

        EventOptions = await db.Events.AsNoTracking()
            .OrderByDescending(e => e.StartsAt).Take(100)
            .Select(e => new ValueTuple<long, string>(e.Id, e.Name))
            .ToListAsync(ct);

        var query = db.IssuanceLogs.AsNoTracking();

        if (EventId is { } eid) query = query.Where(l => l.EventId == eid);

        // 유저 조회는 정확 일치다. LIKE '%…%' 는 (UserId, RequestedAt) 인덱스를 못 타고,
        // 이 테이블은 이 PoC 에서 가장 큰 테이블이다. CS 문의도 유저 ID 를 정확히 안다.
        if (!string.IsNullOrWhiteSpace(UserId)) query = query.Where(l => l.UserId == UserId);

        if (From is { } f) query = query.Where(l => l.RequestedAt >= KoreaTime.ToUtc(f));
        if (To is { } t) query = query.Where(l => l.RequestedAt < KoreaTime.ToUtc(t));
        if (Enum.TryParse<IssueResult>(Result, out var r)) query = query.Where(l => l.Result == r);

        TotalCount = await query.CountAsync(ct);

        Rows = await query
            .OrderByDescending(l => l.RequestedAt).ThenByDescending(l => l.Id)
            .Skip((P - 1) * PageSize).Take(PageSize)
            .ToListAsync(ct);
    }

    public string PageUrl(int page)
    {
        var q = new List<string> { $"p={page}" };
        if (EventId is { } e) q.Add($"eventId={e}");
        if (!string.IsNullOrWhiteSpace(UserId)) q.Add($"userId={Uri.EscapeDataString(UserId)}");
        if (From is { } f) q.Add($"from={f:yyyy-MM-ddTHH:mm}");
        if (To is { } t) q.Add($"to={t:yyyy-MM-ddTHH:mm}");
        if (!string.IsNullOrWhiteSpace(Result)) q.Add($"result={Result}");
        return "/Issuances?" + string.Join("&", q);
    }
}

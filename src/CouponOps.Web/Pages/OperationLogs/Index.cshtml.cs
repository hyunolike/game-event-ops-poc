using CouponOps.Application;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CouponOps.Pages.OperationLogs;

public sealed class IndexModel(AppDbContext db) : PageModel
{
    public const int PageSize = 50;

    [BindProperty(SupportsGet = true)] public string? Actor { get; set; }
    [BindProperty(SupportsGet = true)] public string? Action { get; set; }
    [BindProperty(SupportsGet = true)] public string? TargetId { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public List<OperationLog> Rows { get; private set; } = [];
    public int TotalCount { get; private set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));

    public async Task OnGetAsync(CancellationToken ct)
    {
        P = Math.Max(1, P);

        var query = db.OperationLogs.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(Actor)) query = query.Where(o => o.ActorLoginId == Actor);
        if (Enum.TryParse<OperationAction>(Action, out var a)) query = query.Where(o => o.Action == a);
        if (!string.IsNullOrWhiteSpace(TargetId)) query = query.Where(o => o.TargetId == TargetId);
        if (From is { } f) query = query.Where(o => o.OccurredAt >= KoreaTime.ToUtc(f));
        if (To is { } t) query = query.Where(o => o.OccurredAt < KoreaTime.ToUtc(t));

        TotalCount = await query.CountAsync(ct);

        Rows = await query
            .OrderByDescending(o => o.OccurredAt).ThenByDescending(o => o.Id)
            .Skip((P - 1) * PageSize).Take(PageSize)
            .ToListAsync(ct);
    }

    public string PageUrl(int page)
    {
        var q = new List<string> { $"p={page}" };
        if (!string.IsNullOrWhiteSpace(Actor)) q.Add($"actor={Uri.EscapeDataString(Actor)}");
        if (!string.IsNullOrWhiteSpace(Action)) q.Add($"action={Action}");
        if (!string.IsNullOrWhiteSpace(TargetId)) q.Add($"targetId={Uri.EscapeDataString(TargetId)}");
        if (From is { } f) q.Add($"from={f:yyyy-MM-ddTHH:mm}");
        if (To is { } t) q.Add($"to={t:yyyy-MM-ddTHH:mm}");
        return "/OperationLogs?" + string.Join("&", q);
    }
}

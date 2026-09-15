using CouponOps.Application;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CouponOps.Pages.Events;

[Authorize(Policy = Policies.Editor)]
public sealed class EditModel(AppDbContext db, EventAdminService admin, TimeProvider clock) : PageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public DateTime StartsAt { get; set; }
    [BindProperty] public DateTime EndsAt { get; set; }
    [BindProperty] public int TotalQuantity { get; set; }
    [BindProperty] public int PerUserLimit { get; set; }
    [BindProperty] public string? Reason { get; set; }

    public CouponEvent? Event { get; private set; }
    public EventStatus Status { get; private set; }
    public List<string> Errors { get; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return NotFound();

        Name = Event!.Name;
        StartsAt = KoreaTime.FromUtc(Event.StartsAt);
        EndsAt = KoreaTime.FromUtc(Event.EndsAt);
        TotalQuantity = Event.TotalQuantity;
        PerUserLimit = Event.PerUserLimit;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return NotFound();

        var startsUtc = KoreaTime.ToUtc(StartsAt);
        var endsUtc = KoreaTime.ToUtc(EndsAt);

        Errors.AddRange(CouponEvent.ValidateInput(Event!.Code, Name, startsUtc, endsUtc, TotalQuantity, PerUserLimit));
        if (TotalQuantity > CreateModel.MaxQuantity)
            Errors.Add($"총 발급 수량은 {CreateModel.MaxQuantity:N0}개를 넘을 수 없습니다.");

        if (Errors.Count > 0) return Page();

        try
        {
            await admin.UpdateAsync(Id, Name, startsUtc, endsUtc, TotalQuantity, PerUserLimit, Reason, ct);
        }
        catch (InvalidOperationException ex)
        {
            // 진행 중 이벤트의 감량처럼 도메인 규칙이 막는 경우
            Errors.Add(ex.Message);
            return Page();
        }

        TempData["Flash"] = "이벤트를 수정했습니다.";
        return RedirectToPage("/Events/Details", new { id = Id });
    }

    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        Event = await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.Id == Id, ct);
        if (Event is null) return false;

        Status = Event.StatusAt(clock.GetUtcNow().UtcDateTime);
        return true;
    }
}

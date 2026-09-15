using CouponOps.Application;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using CouponOps.Infrastructure.Redis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CouponOps.Pages.Events;

public sealed class DetailsModel(
    AppDbContext db, IIssuanceStore store, EventAdminService admin,
    ICurrentActor actor, TimeProvider clock) : PageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    // 강제 중단 확인 절차: 이벤트 코드를 직접 입력해야 한다.
    [BindProperty] public string? ConfirmCode { get; set; }
    [BindProperty] public string? Reason { get; set; }

    public CouponEvent? Event { get; private set; }
    public EventStatus Status { get; private set; }
    public long Remaining { get; private set; }
    public long Issued { get; private set; }
    public double Rate { get; private set; }
    public int PersistedSuccess { get; private set; }
    public List<OperationLog> RecentOperations { get; private set; } = [];
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct) =>
        await LoadAsync(ct) ? Page() : NotFound();

    public async Task<IActionResult> OnPostSuspendAsync(CancellationToken ct)
    {
        if (!actor.CanEdit) return Forbid();
        if (!await LoadAsync(ct)) return NotFound();

        // 확인 절차. 되돌리기 어려운 액션이므로 "정말?" 한 번으로는 부족하다 —
        // 옆 탭의 다른 이벤트를 중단시키는 실수를 막으려면 대상을 직접 지목하게 해야 한다.
        if (!string.Equals(ConfirmCode?.Trim(), Event!.Code, StringComparison.Ordinal))
        {
            Error = "확인을 위해 이벤트 코드를 정확히 입력해야 합니다.";
            return Page();
        }

        if (string.IsNullOrWhiteSpace(Reason))
        {
            Error = "중단 사유는 필수입니다. 운영 로그에 남습니다.";
            return Page();
        }

        await admin.SuspendAsync(Id, Reason.Trim(), ct);

        TempData["Flash"] = "이벤트를 강제 중단했습니다. 신규 발급이 즉시 차단됩니다.";
        return RedirectToPage("/Events/Details", new { id = Id });
    }

    public async Task<IActionResult> OnPostResumeAsync(CancellationToken ct)
    {
        if (!actor.CanEdit) return Forbid();
        if (!await LoadAsync(ct)) return NotFound();

        await admin.ResumeAsync(Id, Reason?.Trim(), ct);

        TempData["Flash"] = "이벤트를 재개했습니다.";
        return RedirectToPage("/Events/Details", new { id = Id });
    }

    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        Event = await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.Id == Id, ct);
        if (Event is null) return false;

        var now = clock.GetUtcNow().UtcDateTime;
        Status = Event.StatusAt(now);
        Remaining = await store.GetRemainingAsync(Id, Event.IssuanceMode, ct);
        Issued = Math.Max(0, Event.TotalQuantity - Remaining);
        Rate = Event.TotalQuantity == 0 ? 0 : Math.Round(Issued * 100.0 / Event.TotalQuantity, 1);

        PersistedSuccess = await db.IssuanceLogs
            .CountAsync(l => l.EventId == Id && l.Result == IssueResult.Success, ct);

        RecentOperations = await db.OperationLogs.AsNoTracking()
            .Where(o => o.TargetType == nameof(CouponEvent) && o.TargetId == Id.ToString())
            .OrderByDescending(o => o.OccurredAt)
            .Take(10)
            .ToListAsync(ct);

        return true;
    }
}

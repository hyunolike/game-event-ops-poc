using CouponOps.Application;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using CouponOps.Infrastructure.Redis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CouponOps.Pages.Draws;

public sealed class DetailsModel(
    AppDbContext db, IDrawStore store, DrawAdminService admin,
    ICurrentActor actor, TimeProvider clock) : PageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    // 강제 중단 확인 절차: 이벤트 코드를 직접 입력해야 한다.
    [BindProperty] public string? ConfirmCode { get; set; }
    [BindProperty] public string? Reason { get; set; }

    [BindProperty] public string? GrantUserId { get; set; }
    [BindProperty] public int GrantAmount { get; set; } = 1;

    public DrawEvent? Event { get; private set; }
    public EventStatus Status { get; private set; }
    public List<DrawPrize> Prizes { get; private set; } = [];
    public DeviationReport? Deviation { get; private set; }
    public List<DrawWeightVersion> Versions { get; private set; } = [];
    public long PersistedLogs { get; private set; }
    public List<OperationLog> RecentOperations { get; private set; } = [];
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct) =>
        await LoadAsync(ct) ? Page() : NotFound();

    public async Task<IActionResult> OnPostSuspendAsync(CancellationToken ct)
    {
        if (!actor.CanEdit) return Forbid();
        if (!await LoadAsync(ct)) return NotFound();

        // 확인 절차. 이미 지급된 아이템은 회수할 수 없으므로 "정말?" 한 번으로는 부족하다 —
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

        TempData["Flash"] = "룰렛을 강제 중단했습니다. 신규 추첨이 즉시 차단됩니다.";
        return RedirectToPage("/Draws/Details", new { id = Id });
    }

    public async Task<IActionResult> OnPostResumeAsync(CancellationToken ct)
    {
        if (!actor.CanEdit) return Forbid();
        if (!await LoadAsync(ct)) return NotFound();

        await admin.ResumeAsync(Id, Reason?.Trim(), ct);

        TempData["Flash"] = "룰렛을 재개했습니다.";
        return RedirectToPage("/Draws/Details", new { id = Id });
    }

    public async Task<IActionResult> OnPostGrantAsync(CancellationToken ct)
    {
        if (!actor.CanEdit) return Forbid();
        if (!await LoadAsync(ct)) return NotFound();

        if (string.IsNullOrWhiteSpace(GrantUserId) || GrantAmount <= 0)
        {
            Error = "지급 대상 유저와 1 이상의 수량이 필요합니다.";
            return Page();
        }

        var remaining = await admin.GrantTicketsAsync(Id, GrantUserId.Trim(), GrantAmount, ct);

        TempData["Flash"] = $"{GrantUserId} 에게 티켓 {GrantAmount}개를 지급했습니다 (잔액 {remaining}).";
        return RedirectToPage("/Draws/Details", new { id = Id });
    }

    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        Event = await db.DrawEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Id == Id, ct);
        if (Event is null) return false;

        Status = Event.StatusAt(clock.GetUtcNow().UtcDateTime);

        Prizes = await db.DrawPrizes.AsNoTracking()
            .Where(p => p.DrawEventId == Id).OrderBy(p => p.SlotIndex).ToListAsync(ct);

        var wins = await store.GetWinCountsAsync(Id, ct);
        var stock = await store.GetStockAsync(Id, ct);
        Deviation = DrawDeviation.Compute(Prizes, wins, stock);

        Versions = await db.DrawWeightVersions.AsNoTracking()
            .Where(v => v.DrawEventId == Id)
            .OrderByDescending(v => v.Version)
            .ToListAsync(ct);

        PersistedLogs = await db.DrawLogs
            .CountAsync(l => l.DrawEventId == Id && l.Result == DrawResult.Won, ct);

        RecentOperations = await db.OperationLogs.AsNoTracking()
            .Where(o => o.TargetType == nameof(DrawEvent) && o.TargetId == Id.ToString())
            .OrderByDescending(o => o.OccurredAt)
            .Take(10)
            .ToListAsync(ct);

        return true;
    }
}

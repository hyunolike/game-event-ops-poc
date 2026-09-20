using CouponOps.Application;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CouponOps.Pages.Draws;

/// <summary>
/// 확률 변경 승인 대기함.
/// </summary>
/// <remarks>
/// 조회는 읽기전용 계정도 할 수 있다 — 무엇이 대기 중인지는 감추지 않는다.
/// 승인·반려만 편집 권한을 요구하고, 그마저도 등록자 본인은 할 수 없다.
/// </remarks>
public sealed class ApprovalsModel(DrawApprovalService approvals, ICurrentActor actor) : PageModel
{
    [BindProperty(SupportsGet = true)] public long? DrawEventId { get; set; }

    [BindProperty] public long ApprovalId { get; set; }
    [BindProperty] public string? Note { get; set; }

    public IReadOnlyList<PendingApproval> Rows { get; private set; } = [];
    public string? Error { get; private set; }
    public bool CanEdit => actor.CanEdit;

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostApproveAsync(CancellationToken ct)
    {
        if (!actor.CanEdit) return Forbid();
        return await DecideAsync(() => approvals.ApproveAsync(ApprovalId, Note?.Trim(), ct), ct);
    }

    public async Task<IActionResult> OnPostRejectAsync(CancellationToken ct)
    {
        if (!actor.CanEdit) return Forbid();
        return await DecideAsync(() => approvals.RejectAsync(ApprovalId, Note?.Trim(), ct), ct);
    }

    public async Task<IActionResult> OnPostCancelAsync(CancellationToken ct)
    {
        if (!actor.CanEdit) return Forbid();
        return await DecideAsync(() => approvals.CancelAsync(ApprovalId, ct), ct);
    }

    private async Task<IActionResult> DecideAsync(
        Func<Task<ApprovalResult>> decide, CancellationToken ct)
    {
        var result = await decide();

        switch (result.Outcome)
        {
            case ApprovalOutcome.Approved:
                TempData["Flash"] = "승인했습니다. 새 확률표 버전이 활성화됐습니다.";
                break;
            case ApprovalOutcome.Rejected:
                TempData["Flash"] = "반려했습니다. 확률은 그대로입니다.";
                break;
            case ApprovalOutcome.Cancelled:
                TempData["Flash"] = "요청을 거둬들였습니다.";
                break;
            case ApprovalOutcome.NotAllowed:
                await LoadAsync(ct);
                Error = "본인이 올린 요청은 본인이 결정할 수 없습니다. 이미 결정된 요청일 수도 있습니다.";
                return Page();
            case ApprovalOutcome.Stale:
                await LoadAsync(ct);
                Error = "요청 이후 확률표가 바뀌었습니다. 지금 표 위에 그대로 얹을 수 없으니 다시 요청해야 합니다.";
                return Page();
            default:
                await LoadAsync(ct);
                Error = "요청을 찾을 수 없습니다.";
                return Page();
        }

        return RedirectToPage("/Draws/Approvals", new { drawEventId = DrawEventId });
    }

    private async Task LoadAsync(CancellationToken ct) =>
        Rows = await approvals.ListPendingAsync(DrawEventId, ct);
}

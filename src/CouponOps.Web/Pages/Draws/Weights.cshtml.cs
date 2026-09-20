using CouponOps.Application;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CouponOps.Pages.Draws;

[Authorize(Policy = Policies.Editor)]
public sealed class WeightsModel(
    AppDbContext db, DrawApprovalService approvals, TimeProvider clock) : PageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    /// <summary>슬롯 위치별 새 가중치. 인덱스가 곧 SlotIndex 다.</summary>
    [BindProperty] public List<int> Weights { get; set; } = [];
    [BindProperty] public string? Reason { get; set; }
    [BindProperty] public int SimulationDraws { get; set; } = 10_000;

    public DrawEvent? Event { get; private set; }
    public List<DrawPrize> Prizes { get; private set; } = [];
    public IReadOnlyList<OddsRow> CurrentOdds { get; private set; } = [];
    public IReadOnlyList<OddsRow> ProposedOdds { get; private set; } = [];
    public SimulationReport? Simulation { get; private set; }
    public bool RequiresApproval { get; private set; }
    public int PendingCount { get; private set; }
    public List<string> Errors { get; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return NotFound();

        Weights = Prizes.Select(p => p.Weight).ToList();
        return Page();
    }

    /// <summary>바꾼 값으로 확률과 지급 규모를 먼저 보여준다. 요청 전에 반드시 거치는 단계다.</summary>
    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return NotFound();

        BuildProposal();
        return Page();
    }

    public async Task<IActionResult> OnPostRequestAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return NotFound();

        if (string.IsNullOrWhiteSpace(Reason))
            Errors.Add("변경 사유는 필수입니다. 사유 없는 확률 변경은 사후 조사에서 아무것도 설명하지 못합니다.");

        if (Weights.Count < Prizes.Count)
        {
            Errors.Add("모든 슬롯의 가중치가 필요합니다.");
            return Page();
        }

        var weightBySlot = Weights
            .Select((w, slot) => (slot, w))
            .Where(x => x.slot < Prizes.Count)
            .ToDictionary(x => x.slot, x => x.w);

        // 바뀐 것이 있는지는 반드시 미리보기 전에 본다 —
        // BuildProposal 이 사본 엔티티의 가중치를 제안값으로 덮어쓰기 때문에,
        // 그 뒤에 비교하면 언제나 "같다" 가 나온다.
        var unchanged = Prizes.All(p =>
            weightBySlot.TryGetValue(p.SlotIndex, out var w) && w == p.Weight);

        BuildProposal();

        if (unchanged) Errors.Add("바뀐 가중치가 없습니다.");
        if (Errors.Count > 0) return Page();

        try
        {
            var result = await approvals.RequestWeightChangeAsync(Id, weightBySlot, Reason!.Trim(), ct);

            TempData["Flash"] = result.Outcome switch
            {
                ApprovalOutcome.AppliedImmediately =>
                    "시작 전 이벤트라 확률을 바로 적용했습니다. 새 확률표 버전이 활성화됐습니다.",
                ApprovalOutcome.PendingApproval =>
                    "확률 변경을 요청했습니다. 다른 편집자의 승인이 있어야 적용됩니다 — 본인은 승인할 수 없습니다.",
                _ => "요청을 처리하지 못했습니다.",
            };

            return RedirectToPage("/Draws/Details", new { id = Id });
        }
        catch (ArgumentException ex)
        {
            Errors.AddRange(ex.Message.Split("; "));
            return Page();
        }
    }

    private void BuildProposal()
    {
        if (Weights.Count < Prizes.Count)
        {
            Errors.Add("모든 슬롯의 가중치가 필요합니다.");
            return;
        }

        // 제안된 가중치를 실제 엔티티에 얹어 미리보기를 만든다. 저장하지 않으므로
        // 추적 중인 엔티티가 더럽혀지지 않도록 AsNoTracking 으로 읽은 사본을 쓴다(LoadAsync 참조).
        for (var i = 0; i < Prizes.Count; i++)
            Prizes[i].ApplyPreviewWeight(Weights[i]);

        var errors = DrawEvent.ValidatePrizes(Prizes, Event!.SoldOutPolicy, Event.PityThreshold,
            Event.PityPrizeId is { } pid ? Prizes.SingleOrDefault(p => p.Id == pid)?.SlotIndex : null);
        Errors.AddRange(errors);

        if (errors.Count > 0) return;

        ProposedOdds = DrawOdds.Compute(Prizes);
        Simulation = DrawSimulator.Run(Prizes, Math.Clamp(SimulationDraws, 1, 10_000_000));
    }

    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        Event = await db.DrawEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Id == Id, ct);
        if (Event is null) return false;

        // AsNoTracking — 미리보기로 가중치를 얹어도 저장 경로에 새어 들어가지 않게 한다.
        Prizes = await db.DrawPrizes.AsNoTracking()
            .Where(p => p.DrawEventId == Id).OrderBy(p => p.SlotIndex).ToListAsync(ct);

        CurrentOdds = DrawOdds.Compute(Prizes);
        RequiresApproval = clock.GetUtcNow().UtcDateTime >= Event.StartsAt;
        PendingCount = await db.DrawApprovals
            .CountAsync(a => a.DrawEventId == Id && a.Status == ApprovalStatus.Pending, ct);

        return true;
    }
}

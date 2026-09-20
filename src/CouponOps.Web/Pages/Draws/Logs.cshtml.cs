using CouponOps.Application;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CouponOps.Pages.Draws;

public sealed class LogsModel(AppDbContext db) : PageModel
{
    public const int PageSize = 50;

    [BindProperty(SupportsGet = true)] public long? DrawEventId { get; set; }
    [BindProperty(SupportsGet = true)] public string? UserId { get; set; }
    [BindProperty(SupportsGet = true)] public DrawResult? Result { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;

    [BindProperty] public long VerifyLogId { get; set; }

    public List<DrawLog> Rows { get; private set; } = [];
    public List<DrawEvent> Events { get; private set; } = [];
    public int TotalCount { get; private set; }
    public DrawVerification? Verification { get; private set; }
    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    /// <summary>
    /// 저장된 이력만으로 추첨을 다시 계산해 본다.
    /// </summary>
    /// <remarks>
    /// CS 클레임·내부 감사·규제 대응이 전부 이 버튼에서 끝난다.
    /// 재계산은 <b>대체 치환 전</b>의 슬롯을 돌려주므로, 치환이 있었던 이력은
    /// OriginalPrizeId 와 비교한다 — 치환은 당시 재고에 의존해 순수 함수로 재현되지 않는다.
    /// </remarks>
    public async Task<IActionResult> OnPostVerifyAsync(CancellationToken ct)
    {
        await LoadAsync(ct);

        var log = await db.DrawLogs.AsNoTracking().SingleOrDefaultAsync(l => l.Id == VerifyLogId, ct);
        if (log is null) { Error = "이력을 찾을 수 없습니다."; return Page(); }

        if (log.WeightVersionId is null)
        {
            Error = "확률표를 가리키지 않는 이력입니다(실패 이력). 재현할 대상이 없습니다.";
            return Page();
        }

        var version = await db.DrawWeightVersions.AsNoTracking()
            .SingleOrDefaultAsync(v => v.Id == log.WeightVersionId, ct);
        if (version is null) { Error = "확률표 버전을 찾을 수 없습니다."; return Page(); }

        try
        {
            var snapshot = DrawSnapshot.Deserialize(version.SnapshotJson);
            var recomputed = DrawSnapshot.Recompute(
                snapshot, log.RandomValue, log.PityApplied, log.PrizeId);

            var stored = log.FallbackApplied ? log.OriginalPrizeId ?? 0 : log.PrizeId ?? 0;

            Verification = new DrawVerification(
                log.Id, stored, recomputed, stored == recomputed,
                version.Version, version.SnapshotHash, log.RandomValue, log.Roll,
                log.PityApplied, log.FallbackApplied);
        }
        catch (InvalidOperationException ex)
        {
            Error = $"재현할 수 없습니다: {ex.Message}";
        }

        return Page();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Events = await db.DrawEvents.AsNoTracking()
            .OrderByDescending(e => e.StartsAt).Take(50).ToListAsync(ct);

        var query = db.DrawLogs.AsNoTracking().AsQueryable();

        if (DrawEventId is { } id) query = query.Where(l => l.DrawEventId == id);

        // 정확 일치다. LIKE '%…%' 가 아니다 — 이 테이블은 이 시스템에서 가장 큰 테이블이고
        // (UserId, RequestedAt) 인덱스는 술어가 그것을 쓸 수 있을 때만 도움이 된다.
        // CS 문의도 유저 ID 를 정확히 안다.
        if (!string.IsNullOrWhiteSpace(UserId)) query = query.Where(l => l.UserId == UserId);

        if (Result is { } r) query = query.Where(l => l.Result == r);

        TotalCount = await query.CountAsync(ct);

        PageNumber = Math.Max(1, PageNumber);
        Rows = await query
            .OrderByDescending(l => l.RequestedAt)
            .Skip((PageNumber - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync(ct);
    }
}

using CouponOps.Application;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CouponOps.Pages;

/// <summary>
/// 확률 공시. 로그인 없이 열린다.
/// </summary>
/// <remarks>
/// 운영툴의 미리보기와 <b>같은 함수</b>(<see cref="DrawOdds.Compute"/>)로 계산한다.
/// 어드민 확률표와 공시 페이지가 다른 소스를 보는 순간, 언젠가 반드시 어긋난다.
/// 수기 동기화는 실무 사고의 단골이다.
/// </remarks>
[AllowAnonymous]
public sealed class OddsModel(AppDbContext db, TimeProvider clock) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Code { get; set; }

    public DrawEvent? Event { get; private set; }
    public EventStatus Status { get; private set; }
    public IReadOnlyList<OddsRow> Rows { get; private set; } = [];
    public DrawWeightVersion? Version { get; private set; }
    public string SoldOutNotice { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Code)) return NotFound();

        Event = await db.DrawEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Code == Code, ct);
        if (Event is null) return NotFound();

        Status = Event.StatusAt(clock.GetUtcNow().UtcDateTime);

        var prizes = await db.DrawPrizes.AsNoTracking()
            .Where(p => p.DrawEventId == Event.Id).ToListAsync(ct);

        Rows = DrawOdds.Compute(prizes);

        if (Event.ActiveWeightVersionId is { } vid)
            Version = await db.DrawWeightVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == vid, ct);

        SoldOutNotice = DrawOdds.SoldOutNotice(prizes);
        return Page();
    }
}

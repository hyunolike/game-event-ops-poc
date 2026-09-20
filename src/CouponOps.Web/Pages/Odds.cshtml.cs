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

        SoldOutNotice = BuildNotice(prizes);
        return Page();
    }

    /// <summary>
    /// 소진 정책 문구.
    /// </summary>
    /// <remarks>
    /// 대체(Fallback) 정책이므로 "소진되면 확률이 바뀐다" 가 아니라 "무엇으로 대체되는가" 를 밝힌다.
    /// 표시된 확률은 재고와 무관하게 언제나 실행 확률과 같다 — 그것이 이 정책을 택한 이유다.
    /// </remarks>
    private static string BuildNotice(IReadOnlyList<DrawPrize> prizes)
    {
        var limited = prizes.Where(p => !p.IsUnlimited).ToList();
        if (limited.Count == 0) return "";

        var byId = prizes.ToDictionary(p => p.Id);
        var replacements = limited
            .Select(p => p.FallbackPrizeId)
            .Where(id => id is not null)
            .Select(id => byId.TryGetValue(id!.Value, out var f) ? f.Name : null)
            .Where(n => n is not null)
            .Distinct()
            .ToList();

        return replacements.Count == 0
            ? "한정 수량 경품은 소진 시 지급되지 않습니다."
            : $"한정 수량 경품 소진 시 해당 확률은 '{string.Join(", ", replacements)}' 지급으로 대체됩니다. "
              + "표시된 확률은 소진 여부와 무관하게 변하지 않습니다.";
    }
}

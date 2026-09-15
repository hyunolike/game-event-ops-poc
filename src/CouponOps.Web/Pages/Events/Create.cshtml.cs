using CouponOps.Application;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CouponOps.Pages.Events;

[Authorize(Policy = Policies.Editor)]
public sealed class CreateModel(AppDbContext db, EventAdminService admin, TimeProvider clock) : PageModel
{
    /// <summary>
    /// 사전 생성 방식의 상한. 이보다 크면 코드 생성·적재에 수 분이 걸려 폼 요청이 타임아웃된다.
    /// 그 규모는 백그라운드 작업으로 분리해야 하며, 이 PoC 의 범위를 벗어난다.
    /// </summary>
    public const int MaxQuantity = 1_000_000;

    [BindProperty] public string Code { get; set; } = "";
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public DateTime StartsAt { get; set; }
    [BindProperty] public DateTime EndsAt { get; set; }
    [BindProperty] public int TotalQuantity { get; set; } = 1000;
    [BindProperty] public int PerUserLimit { get; set; } = 1;
    [BindProperty] public IssuanceMode IssuanceMode { get; set; } = IssuanceMode.PreGenerated;

    public List<string> Errors { get; } = [];

    public void OnGet()
    {
        var nowKst = KoreaTime.FromUtc(clock.GetUtcNow().UtcDateTime);
        StartsAt = nowKst.AddHours(1).AddMinutes(-nowKst.Minute).AddSeconds(-nowKst.Second);
        EndsAt = StartsAt.AddDays(7);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        // 폼의 datetime-local 은 KST 로 입력된다. 경계에서 UTC 로 바꿔 저장한다.
        var startsUtc = KoreaTime.ToUtc(StartsAt);
        var endsUtc = KoreaTime.ToUtc(EndsAt);

        Errors.AddRange(CouponEvent.ValidateInput(Code, Name, startsUtc, endsUtc, TotalQuantity, PerUserLimit));

        if (TotalQuantity > MaxQuantity)
            Errors.Add($"총 발급 수량은 {MaxQuantity:N0}개를 넘을 수 없습니다 (사전 생성 시간 제약).");

        if (await db.Events.AnyAsync(e => e.Code == Code, ct))
            Errors.Add($"이벤트 코드 '{Code}' 는 이미 사용 중입니다.");

        if (Errors.Count > 0) return Page();

        var ev = await admin.CreateAndWarmAsync(
            Code, Name, startsUtc, endsUtc, TotalQuantity, PerUserLimit, IssuanceMode, ct);

        TempData["Flash"] = $"이벤트 '{ev.Name}' 를 생성하고 {ev.TotalQuantity:N0}개를 준비했습니다.";
        return RedirectToPage("/Events/Details", new { id = ev.Id });
    }
}

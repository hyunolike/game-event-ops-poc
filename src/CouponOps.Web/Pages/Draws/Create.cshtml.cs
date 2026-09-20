using System.Security.Cryptography;
using System.Text;
using CouponOps.Application;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CouponOps.Pages.Draws;

/// <summary>운영자가 입력하는 슬롯 한 줄. 경품명이 비어 있으면 사용하지 않는 줄로 본다.</summary>
public sealed class SlotInput
{
    public string? Name { get; set; }
    public long ItemId { get; set; }
    public int ItemQty { get; set; } = 1;
    public int Weight { get; set; }
    /// <summary>-1 = 무제한.</summary>
    public int Stock { get; set; } = -1;
    public bool IsJackpot { get; set; }
    public bool IsBlank { get; set; }

    public bool IsUsed => !string.IsNullOrWhiteSpace(Name);
}

[Authorize(Policy = Policies.Editor)]
public sealed class CreateModel(AppDbContext db, DrawAdminService admin, TimeProvider clock) : PageModel
{
    /// <summary>폼에 그리는 슬롯 줄 수. 돌림판은 보통 6~12칸이다.</summary>
    public const int SlotRows = 8;

    /// <summary>시뮬레이션 기본 횟수. 이벤트 참여 규모를 가늠해 넣는 값이다.</summary>
    public const int DefaultSimulationDraws = 10_000;

    [BindProperty] public string Code { get; set; } = "";
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public DateTime StartsAt { get; set; }
    [BindProperty] public DateTime EndsAt { get; set; }
    [BindProperty] public int DailyDrawLimit { get; set; } = 3;
    [BindProperty] public int DailyResetHour { get; set; } = 4;
    [BindProperty] public long? TicketItemId { get; set; }
    [BindProperty] public int TicketCost { get; set; }
    [BindProperty] public int PityThreshold { get; set; }
    [BindProperty] public int PitySlot { get; set; } = -1;

    /// <summary>
    /// 모든 유한 재고 슬롯이 공유하는 대체 경품.
    /// </summary>
    /// <remarks>
    /// 슬롯마다 따로 고르게 하지 않는 이유는, 도메인 규칙이 이미 "대체 대상은 무제한 재고" 로
    /// 못박혀 있기 때문이다. 선택지를 하나로 모으면 잘못 고를 방법 자체가 없어진다.
    /// </remarks>
    [BindProperty] public int FallbackSlot { get; set; } = -1;

    [BindProperty] public List<SlotInput> Slots { get; set; } = [];
    [BindProperty] public int SimulationDraws { get; set; } = DefaultSimulationDraws;

    /// <summary>
    /// 마지막으로 본 시뮬레이션이 대상으로 삼은 설정의 지문.
    /// </summary>
    /// <remarks>
    /// 이것이 게이트다 — 지금 폼의 지문과 다르면 생성을 막고 시뮬레이션을 다시 보여준다.
    /// "가중치 5 를 50 으로 잘못 친" 사고는 시뮬레이션 화면에서만 걸리는데,
    /// 건너뛸 수 있는 정보성 화면으로 두면 아무도 보지 않는다.
    /// </remarks>
    [BindProperty] public string? PreviewSignature { get; set; }

    public SimulationReport? Simulation { get; private set; }
    public IReadOnlyList<OddsRow> Odds { get; private set; } = [];
    public List<string> Errors { get; } = [];

    public void OnGet()
    {
        var nowKst = KoreaTime.FromUtc(clock.GetUtcNow().UtcDateTime);
        StartsAt = nowKst.AddHours(1).AddMinutes(-nowKst.Minute).AddSeconds(-nowKst.Second);
        EndsAt = StartsAt.AddDays(14);
        Slots = DefaultSlots();
        FallbackSlot = 3;
    }

    public IActionResult OnPostPreview()
    {
        Normalize();
        var prizes = BuildPrizes();
        if (Errors.Count > 0) return Page();

        Odds = DrawOdds.Compute(prizes);
        Simulation = DrawSimulator.Run(prizes, Math.Clamp(SimulationDraws, 1, 10_000_000));
        PreviewSignature = Signature();
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        Normalize();
        var prizes = BuildPrizes();

        if (await db.DrawEvents.AnyAsync(e => e.Code == Code, ct))
            Errors.Add($"이벤트 코드 '{Code}' 는 이미 사용 중입니다.");

        if (Errors.Count == 0 && PreviewSignature != Signature())
        {
            Errors.Add("설정이 바뀌었습니다. 시뮬레이션 결과를 확인한 뒤 생성하세요.");
            Odds = DrawOdds.Compute(prizes);
            Simulation = DrawSimulator.Run(prizes, Math.Clamp(SimulationDraws, 1, 10_000_000));
            PreviewSignature = Signature();
            return Page();
        }

        if (Errors.Count > 0) return Page();

        try
        {
            var ev = await admin.CreateAndWarmAsync(
                Code, Name,
                KoreaTime.ToUtc(StartsAt), KoreaTime.ToUtc(EndsAt),
                DailyDrawLimit, TimeSpan.FromHours(DailyResetHour),
                TicketItemId, TicketCost,
                PityThreshold, PityThreshold > 0 ? PitySlot : null,
                prizes, ct);

            TempData["Flash"] = $"룰렛 '{ev.Name}' 를 생성하고 {prizes.Count}개 슬롯을 준비했습니다.";
            return RedirectToPage("/Draws/Details", new { id = ev.Id });
        }
        catch (ArgumentException ex)
        {
            Errors.AddRange(ex.Message.Split("; "));
            return Page();
        }
    }

    /// <summary>빈 줄을 걷어내고 슬롯 위치를 0부터 다시 매긴다.</summary>
    private void Normalize()
    {
        Slots = Slots.Where(s => s.IsUsed).ToList();
        while (Slots.Count < SlotRows) Slots.Add(new SlotInput());
    }

    private List<DrawPrize> BuildPrizes()
    {
        var used = Slots.Where(s => s.IsUsed).ToList();
        if (used.Count < 2)
        {
            Errors.Add("경품은 2개 이상이어야 합니다.");
            return [];
        }

        var hasFinite = used.Any(s => s.Stock >= 0);
        if (hasFinite && (FallbackSlot < 0 || FallbackSlot >= used.Count))
            Errors.Add("한정 재고 슬롯이 있으면 대체 경품을 지정해야 합니다.");
        else if (hasFinite && used[FallbackSlot].Stock >= 0)
            Errors.Add("대체 경품은 재고 무제한 슬롯이어야 합니다.");

        if (PityThreshold > 0 && (PitySlot < 0 || PitySlot >= used.Count))
            Errors.Add("천장을 사용하면 천장 지급 경품을 지정해야 합니다.");

        return used.Select((s, i) => DrawPrize.Create(
            slotIndex: i,
            name: s.Name!.Trim(),
            itemId: s.IsBlank ? 0 : s.ItemId,
            itemQty: s.IsBlank ? 0 : s.ItemQty,
            weight: s.Weight,
            initialStock: s.Stock,
            isJackpot: s.IsJackpot,
            isBlank: s.IsBlank,
            fallbackSlotIndex: s.Stock >= 0 && FallbackSlot >= 0 && FallbackSlot < used.Count
                ? FallbackSlot
                : null)).ToList();
    }

    /// <summary>시뮬레이션이 대상으로 삼은 설정의 지문. 한 글자라도 바뀌면 달라진다.</summary>
    private string Signature()
    {
        var raw = string.Join(';', Slots.Where(s => s.IsUsed).Select(s =>
            $"{s.Name}|{s.ItemId}|{s.ItemQty}|{s.Weight}|{s.Stock}|{s.IsJackpot}|{s.IsBlank}"))
            + $"#{FallbackSlot}#{PityThreshold}#{PitySlot}#{SimulationDraws}";

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
    }

    private static List<SlotInput> DefaultSlots() =>
    [
        new() { Name = "전설 무기 상자", ItemId = 9001, ItemQty = 1, Weight = 5, Stock = 10, IsJackpot = true },
        new() { Name = "영웅 무기 상자", ItemId = 9002, ItemQty = 1, Weight = 45, Stock = 100 },
        new() { Name = "강화 주문서", ItemId = 3001, ItemQty = 5, Weight = 200, Stock = -1 },
        new() { Name = "골드", ItemId = 1001, ItemQty = 1000, Weight = 750, Stock = -1 },
        new(), new(), new(), new(),
    ];
}

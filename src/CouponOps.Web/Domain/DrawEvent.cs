using System.Linq.Expressions;

namespace CouponOps.Domain;

/// <summary>
/// 룰렛(확률 지급) 이벤트.
/// </summary>
/// <remarks>
/// 기간·중단·파생 상태 규칙은 <see cref="CouponEvent"/> 와 동일하며 <see cref="EventSchedule"/> 을 공유한다.
/// 쿠폰과 다른 점은 셋이다 — 결과가 확정적이지 않고, 재고가 경품별로 여럿이며, 입장 비용이 있다.
/// 설계 근거는 docs/04-roulette-design.md.
/// </remarks>
public class DrawEvent
{
    private DrawEvent() { }

    public long Id { get; private set; }
    public string Code { get; private set; } = default!;
    public string Name { get; private set; } = default!;

    public DateTime StartsAt { get; private set; }   // UTC
    public DateTime EndsAt { get; private set; }     // UTC

    /// <summary>유저당 1일 추첨 횟수. 0 = 무제한.</summary>
    public int DailyDrawLimit { get; private set; }

    /// <summary>
    /// 일일 카운터 리셋 기준 시각(KST). 게임 이벤트는 자정이 아니라 새벽(보통 04~06시)에 리셋한다 —
    /// 자정에 리셋하면 밤늦게 접속한 유저가 "어제 몫" 을 못 쓰고 날린다.
    /// </summary>
    public TimeSpan DailyResetAt { get; private set; }

    /// <summary>입장에 소모할 아이템. null = 무료(일일 횟수로만 제한).</summary>
    public long? TicketItemId { get; private set; }
    public int TicketCost { get; private set; }

    public SoldOutPolicy SoldOutPolicy { get; private set; }

    /// <summary>천장. 0 = 사용 안 함. 꽝이 이 횟수만큼 연속되면 <see cref="PityPrizeId"/> 를 확정 지급한다.</summary>
    public int PityThreshold { get; private set; }
    public long? PityPrizeId { get; private set; }

    /// <summary>현재 활성 가중치 버전. 추첨은 항상 이 버전을 기준으로 하고, 이력에 그대로 기록된다.</summary>
    public long? ActiveWeightVersionId { get; private set; }

    public DateTime? SuspendedAt { get; private set; }
    public string? SuspendReason { get; private set; }
    public DateTime? PoolWarmedAt { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = default!;

    public static DrawEvent Create(
        string code, string name, DateTime startsAtUtc, DateTime endsAtUtc,
        int dailyDrawLimit, TimeSpan dailyResetAt, long? ticketItemId, int ticketCost,
        SoldOutPolicy policy, int pityThreshold, DateTime nowUtc)
    {
        var e = new DrawEvent
        {
            Code = code, Name = name, StartsAt = startsAtUtc, EndsAt = endsAtUtc,
            DailyDrawLimit = dailyDrawLimit, DailyResetAt = dailyResetAt,
            TicketItemId = ticketItemId, TicketCost = ticketCost,
            SoldOutPolicy = policy, PityThreshold = pityThreshold,
            CreatedAt = nowUtc, UpdatedAt = nowUtc,
        };

        var errors = ValidateInput(code, name, startsAtUtc, endsAtUtc,
                                   dailyDrawLimit, dailyResetAt, ticketItemId, ticketCost, policy, pityThreshold);
        if (errors.Count > 0) throw new ArgumentException(string.Join("; ", errors));
        return e;
    }

    /// <summary>
    /// 이벤트 자체의 입력값 검증. 경품 구성은 별도다 — <see cref="ValidatePrizes"/> 참조.
    /// 엔티티를 건드리기 전에 호출할 수 있도록 static 으로 둔다.
    /// </summary>
    public static IReadOnlyList<string> ValidateInput(
        string code, string name, DateTime startsAt, DateTime endsAt,
        int dailyDrawLimit, TimeSpan dailyResetAt, long? ticketItemId, int ticketCost,
        SoldOutPolicy policy, int pityThreshold)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(code)) errors.Add("이벤트 코드는 필수입니다.");
        if (string.IsNullOrWhiteSpace(name)) errors.Add("이벤트 이름은 필수입니다.");
        if (endsAt <= startsAt) errors.Add("종료 시각은 시작 시각보다 뒤여야 합니다.");
        if (dailyDrawLimit < 0) errors.Add("일일 추첨 횟수는 0(무제한) 이상이어야 합니다.");
        if (dailyResetAt < TimeSpan.Zero || dailyResetAt >= TimeSpan.FromDays(1))
            errors.Add("일일 리셋 시각은 00:00 이상 24:00 미만이어야 합니다.");
        if (ticketCost < 0) errors.Add("티켓 비용은 0 이상이어야 합니다.");
        if (ticketItemId is null && ticketCost > 0) errors.Add("티켓 비용이 있으면 티켓 아이템이 필요합니다.");
        if (ticketItemId is not null && ticketCost <= 0) errors.Add("티켓 아이템을 지정하면 비용은 1 이상이어야 합니다.");
        if (dailyDrawLimit == 0 && ticketItemId is null)
            // 둘 다 없으면 한 유저가 이벤트 기간 내내 무한히 돌릴 수 있다. 재고가 무제한인 슬롯은
            // 실제로 무한 지급된다 — 설정 실수로 가장 흔한 형태라 아예 막는다.
            errors.Add("일일 횟수 제한과 티켓 비용 중 최소 하나는 있어야 합니다.");
        if (pityThreshold < 0) errors.Add("천장 횟수는 0(사용 안 함) 이상이어야 합니다.");
        if (policy == SoldOutPolicy.Renormalize)
            errors.Add("재분배(Renormalize) 정책은 공시 확률과 실행 확률이 달라지므로 지원하지 않습니다. "
                       + "대체(Fallback) 정책을 사용하세요.");
        return errors;
    }

    /// <summary>
    /// 경품 구성 검증.
    /// </summary>
    /// <remarks>
    /// 대체 대상을 "무제한 재고" 로 못박는 것이 핵심이다. 유한 재고끼리 대체를 걸면
    /// 치환이 연쇄되고 순환할 수 있으며, 그 결과 공시 확률과 실행 확률의 관계를 설명할 수 없게 된다.
    /// </remarks>
    public static IReadOnlyList<string> ValidatePrizes(
        IReadOnlyList<DrawPrize> prizes, SoldOutPolicy policy, int pityThreshold, int? pitySlotIndex)
    {
        var errors = new List<string>();

        if (prizes.Count < 2) { errors.Add("경품은 2개 이상이어야 합니다."); return errors; }

        for (var i = 0; i < prizes.Count; i++)
        {
            var p = prizes[i];
            if (string.IsNullOrWhiteSpace(p.Name)) errors.Add($"{i + 1}번 슬롯: 경품명은 필수입니다.");
            if (p.Weight <= 0) errors.Add($"{i + 1}번 슬롯: 가중치는 1 이상이어야 합니다.");
            if (p.InitialStock < DrawPrize.Unlimited)
                errors.Add($"{i + 1}번 슬롯: 재고는 0 이상이거나 -1(무제한)이어야 합니다.");
            if (!p.IsBlank && p.ItemQty <= 0) errors.Add($"{i + 1}번 슬롯: 지급 수량은 1 이상이어야 합니다.");
        }

        var slots = prizes.Select(p => p.SlotIndex).ToList();
        if (slots.Distinct().Count() != slots.Count) errors.Add("슬롯 위치가 중복됩니다.");
        if (slots.OrderBy(s => s).SequenceEqual(Enumerable.Range(0, prizes.Count)) is false)
            errors.Add($"슬롯 위치는 0부터 {prizes.Count - 1} 까지 빠짐없이 있어야 합니다.");

        var total = prizes.Sum(p => (long)p.Weight);
        if (total <= 0) errors.Add("가중치 합이 0보다 커야 합니다.");
        if (total > int.MaxValue) errors.Add("가중치 합이 너무 큽니다.");

        if (policy == SoldOutPolicy.Fallback)
        {
            var unlimited = prizes.Where(p => p.IsUnlimited).Select(p => p.SlotIndex).ToHashSet();
            if (unlimited.Count == 0)
                errors.Add("대체 정책에서는 재고 무제한 슬롯이 최소 하나 필요합니다(대체 대상).");

            // 생성 시에는 슬롯 위치로, 저장된 뒤에는 FK 로 대체 경품을 가리킨다. 둘을 하나로 본다.
            var slotByPrizeId = prizes.Where(p => p.Id != 0).ToDictionary(p => p.Id, p => p.SlotIndex);

            foreach (var p in prizes.Where(p => !p.IsUnlimited))
            {
                var fallbackSlot = p.EffectiveFallbackSlot(slotByPrizeId);
                if (fallbackSlot is null)
                    errors.Add($"{p.SlotIndex + 1}번 슬롯 '{p.Name}': 유한 재고 슬롯에는 대체 경품이 필요합니다.");
                else if (!unlimited.Contains(fallbackSlot.Value))
                    errors.Add($"{p.SlotIndex + 1}번 슬롯 '{p.Name}': 대체 경품은 재고 무제한 슬롯이어야 합니다.");
            }
        }

        if (pityThreshold > 0)
        {
            if (pitySlotIndex is null) errors.Add("천장을 사용하면 천장 지급 경품을 지정해야 합니다.");
            else if (!slots.Contains(pitySlotIndex.Value)) errors.Add("천장 지급 경품이 슬롯 목록에 없습니다.");
            else if (prizes.Single(p => p.SlotIndex == pitySlotIndex.Value).IsBlank)
                errors.Add("천장 지급 경품으로 꽝을 지정할 수 없습니다.");
        }

        if (prizes.All(p => p.IsBlank)) errors.Add("모든 슬롯이 꽝일 수 없습니다.");

        return errors;
    }

    public void Update(string name, DateTime startsAtUtc, DateTime endsAtUtc,
                       int dailyDrawLimit, TimeSpan dailyResetAt, long? ticketItemId, int ticketCost,
                       int pityThreshold, DateTime nowUtc)
    {
        var errors = ValidateInput(Code, name, startsAtUtc, endsAtUtc, dailyDrawLimit, dailyResetAt,
                                   ticketItemId, ticketCost, SoldOutPolicy, pityThreshold);
        if (errors.Count > 0) throw new ArgumentException(string.Join("; ", errors));

        Name = name;
        StartsAt = startsAtUtc;
        EndsAt = endsAtUtc;
        DailyDrawLimit = dailyDrawLimit;
        DailyResetAt = dailyResetAt;
        TicketItemId = ticketItemId;
        TicketCost = ticketCost;
        PityThreshold = pityThreshold;
        UpdatedAt = nowUtc;
    }

    public EventStatus StatusAt(DateTime utcNow) =>
        EventSchedule.StatusAt(SuspendedAt, StartsAt, EndsAt, utcNow);

    /// <summary>
    /// <see cref="StatusAt"/> 와 동일한 규칙의 SQL 술어. 목록 필터가 서버 측 WHERE 절이 된다.
    /// 규칙이 두 곳에 있으므로 바꿀 때는 <see cref="EventSchedule.StatusAt"/> 도 함께 본다.
    /// </summary>
    public static Expression<Func<DrawEvent, bool>> IsStatus(EventStatus status, DateTime utcNow) =>
        status switch
        {
            EventStatus.Suspended => e => e.SuspendedAt != null,
            EventStatus.Scheduled => e => e.SuspendedAt == null && utcNow < e.StartsAt,
            EventStatus.Ended     => e => e.SuspendedAt == null && utcNow >= e.EndsAt,
            _                     => e => e.SuspendedAt == null && utcNow >= e.StartsAt && utcNow < e.EndsAt,
        };

    public void Suspend(DateTime nowUtc, string reason)
    {
        SuspendedAt = nowUtc;
        SuspendReason = reason;
        UpdatedAt = nowUtc;
    }

    public void Resume(DateTime nowUtc)
    {
        SuspendedAt = null;
        SuspendReason = null;
        UpdatedAt = nowUtc;
    }

    public void SetPityPrize(long? prizeId, DateTime nowUtc)
    {
        PityPrizeId = prizeId;
        UpdatedAt = nowUtc;
    }

    public void ActivateWeightVersion(long weightVersionId, DateTime nowUtc)
    {
        ActiveWeightVersionId = weightVersionId;
        UpdatedAt = nowUtc;
    }

    public void MarkPoolWarmed(DateTime nowUtc)
    {
        PoolWarmedAt = nowUtc;
        UpdatedAt = nowUtc;
    }
}

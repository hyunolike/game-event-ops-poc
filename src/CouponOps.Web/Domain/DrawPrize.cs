namespace CouponOps.Domain;

/// <summary>
/// 돌림판의 슬롯 하나. 가중치는 <b>정수</b>로 저장한다.
/// </summary>
/// <remarks>
/// 확률(0.005)이 아니라 가중치(5)를 저장하는 이유는 셋이다 — 설계 문서 1장 결정 1.
/// <list type="number">
/// <item>부동소수 합산 오차가 없어 "합이 정확히 100%" 를 검증할 수 있다.</item>
/// <item>슬롯을 추가할 때 나머지 슬롯을 건드리지 않아도 된다.</item>
/// <item>추첨이 정수 나머지 연산으로 끝난다 — Lua 안에서 부동소수를 쓰지 않는다.</item>
/// </list>
/// 확률은 <c>Weight / TotalWeight</c> 로 <b>표시할 때</b> 계산한다. 저장하지 않는다.
/// </remarks>
public class DrawPrize
{
    private DrawPrize() { }

    /// <summary>재고 무제한을 뜻하는 <see cref="InitialStock"/> 값.</summary>
    public const int Unlimited = -1;

    public long Id { get; private set; }
    public long DrawEventId { get; private set; }

    /// <summary>돌림판 화면상의 위치. 0부터 연속이어야 한다(클라이언트가 이 값으로 멈출 칸을 정한다).</summary>
    public int SlotIndex { get; private set; }
    public string Name { get; private set; } = default!;

    public long ItemId { get; private set; }
    public int ItemQty { get; private set; }

    public int Weight { get; private set; }

    /// <summary>남은 재고의 초기값. <see cref="Unlimited"/>(-1) 이면 무제한.</summary>
    public int InitialStock { get; private set; }

    /// <summary>
    /// 소진 시 대체할 경품. <see cref="SoldOutPolicy.Fallback"/> 에서 유한 재고 슬롯이면 필수다.
    /// 대체 대상은 반드시 무제한 재고여야 한다 — 그래야 치환이 한 번에 끝나고 순환이 생기지 않는다.
    /// </summary>
    public long? FallbackPrizeId { get; private set; }

    /// <summary>
    /// 대체 경품을 슬롯 위치로 가리키는 입력값. <b>DB 에 매핑되지 않는다</b>(AppDbContext 에서 Ignore).
    /// 생성 시점에는 아직 Id 가 없어 <see cref="FallbackPrizeId"/> 로 검증할 수 없으므로,
    /// 운영자 입력과 검증은 슬롯 위치로 하고 저장 직후 Id 로 해소한다.
    /// </summary>
    public int? FallbackSlotIndex { get; private set; }

    /// <summary>잭팟 표시. 2인 승인·이상 탐지·알림의 트리거다.</summary>
    public bool IsJackpot { get; private set; }

    /// <summary>꽝. 지급이 없고 천장 카운터를 증가시킨다.</summary>
    public bool IsBlank { get; private set; }

    public static DrawPrize Create(
        int slotIndex, string name, long itemId, int itemQty,
        int weight, int initialStock, bool isJackpot, bool isBlank, int? fallbackSlotIndex = null) =>
        new()
        {
            SlotIndex = slotIndex, Name = name, ItemId = itemId, ItemQty = itemQty,
            Weight = weight, InitialStock = initialStock, IsJackpot = isJackpot, IsBlank = isBlank,
            FallbackSlotIndex = fallbackSlotIndex,
        };

    public bool IsUnlimited => InitialStock == Unlimited;

    /// <summary>
    /// 이 경품이 가리키는 대체 슬롯. 생성 시에는 입력값(<see cref="FallbackSlotIndex"/>)이,
    /// 저장된 뒤에는 FK 가 진실이므로 둘을 하나로 본다.
    /// </summary>
    public int? EffectiveFallbackSlot(IReadOnlyDictionary<long, int> slotByPrizeId) =>
        FallbackSlotIndex
        ?? (FallbackPrizeId is { } id && slotByPrizeId.TryGetValue(id, out var slot) ? slot : null);

    internal void AttachTo(long drawEventId) => DrawEventId = drawEventId;

    /// <summary>
    /// 가중치 변경. 호출자는 반드시 새 <see cref="DrawWeightVersion"/> 을 함께 활성화해야 한다 —
    /// 버전 없이 바꾸면 그전 추첨의 확률표가 사라진다.
    /// </summary>
    internal void ChangeWeight(int weight) => Weight = weight;

    /// <summary>
    /// 미리보기 전용. 추적되지 않는(AsNoTracking) 사본에 제안된 가중치를 얹어
    /// 확률·시뮬레이션을 계산할 때만 쓴다 — 저장 경로에서는 <see cref="ChangeWeight"/> 를 쓴다.
    /// 이름을 나눠 두는 이유는, 미리보기가 실수로 저장 경로에 섞여 들어가는 것을 막기 위해서다.
    /// </summary>
    public void ApplyPreviewWeight(int weight) => Weight = weight;

    /// <summary>저장으로 Id 가 확보된 뒤 슬롯 위치를 실제 FK 로 해소한다.</summary>
    internal void ResolveFallback(IReadOnlyDictionary<int, long> idBySlot) =>
        FallbackPrizeId = FallbackSlotIndex is { } slot ? idBySlot[slot] : null;
}

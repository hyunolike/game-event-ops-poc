namespace CouponOps.Domain;

/// <summary>
/// 당첨 보상 우편함.
/// </summary>
/// <remarks>
/// 추첨 결과를 인벤토리에 직접 꽂지 않고 우편함을 거치는 이유는 넷이다.
/// <list type="number">
/// <item>인벤토리가 가득 찬 유저·오프라인 유저를 처리할 수 있다.</item>
/// <item>잘못 설정된 경품을 <b>미수령분만</b> 회수할 수 있다. 인벤토리에 들어간 뒤에는 불가능하다.</item>
/// <item>유저에게 "받았다" 는 명시적 확인 UX 를 준다 — CS 문의의 절반이 여기서 사라진다.</item>
/// <item>추첨(빠른 경로)과 인벤토리 반영(느린 경로)을 분리한다.</item>
/// </list>
/// 이 PoC 는 우편함까지만 만든다. 수령 시 인벤토리 반영은 게임 서버의 몫이다.
/// </remarks>
public class DrawRewardMail
{
    private DrawRewardMail() { }

    public long Id { get; private set; }
    /// <summary>발송 멱등 키. 추첨 요청 Id 를 그대로 쓴다 — 재처리로 우편이 두 통 가지 않는다.</summary>
    public Guid RequestId { get; private set; }
    public long DrawEventId { get; private set; }
    public string UserId { get; private set; } = default!;

    public long PrizeId { get; private set; }
    public string PrizeName { get; private set; } = default!;
    public long ItemId { get; private set; }
    public int ItemQty { get; private set; }

    public DateTime CreatedAt { get; private set; }
    /// <summary>수령 시각. null 이면 미수령 — 회수 가능한 상태다.</summary>
    public DateTime? ClaimedAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }
    public string? RevokeReason { get; private set; }

    public static DrawRewardMail Create(
        Guid requestId, long drawEventId, string userId,
        long prizeId, string prizeName, long itemId, int itemQty, DateTime createdAtUtc) =>
        new()
        {
            RequestId = requestId, DrawEventId = drawEventId, UserId = userId,
            PrizeId = prizeId, PrizeName = prizeName, ItemId = itemId, ItemQty = itemQty,
            CreatedAt = createdAtUtc,
        };

    /// <summary>수령 처리. 이미 수령했거나 회수된 우편은 false 를 돌려주고 아무것도 바꾸지 않는다.</summary>
    public bool TryClaim(DateTime nowUtc)
    {
        if (ClaimedAt is not null || RevokedAt is not null) return false;
        ClaimedAt = nowUtc;
        return true;
    }

    /// <summary>미수령분 회수. 이미 수령한 우편은 회수할 수 없다 — 아이템이 이미 유저 손에 있다.</summary>
    public bool TryRevoke(DateTime nowUtc, string reason)
    {
        if (ClaimedAt is not null || RevokedAt is not null) return false;
        RevokedAt = nowUtc;
        RevokeReason = reason;
        return true;
    }
}

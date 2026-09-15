namespace CouponOps.Domain;

/// <summary>이벤트에 속한 쿠폰 한 장.</summary>
public class Coupon
{
    private Coupon() { }

    public long Id { get; private set; }
    public long EventId { get; private set; }
    public string Code { get; private set; } = default!;   // 전역 UNIQUE
    public CouponStatus Status { get; private set; }
    public string? IssuedToUserId { get; private set; }
    public DateTime? IssuedAt { get; private set; }
    public DateTime? UsedAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }
    public string? RevokeReason { get; private set; }
    public byte[] RowVersion { get; private set; } = default!;

    /// <summary>사전 생성(PreGenerated) 모드에서 이벤트 오픈 전에 만들어 두는 미발급 쿠폰.</summary>
    public static Coupon CreateUnissued(long eventId, string code) =>
        new() { EventId = eventId, Code = code, Status = CouponStatus.Unissued };

    /// <summary>파생(Derived) 모드에서 발급 시점에 생성되는 쿠폰. 이미 발급된 상태로 만들어진다.</summary>
    public static Coupon CreateIssued(long eventId, string code, string userId, DateTime issuedAtUtc) =>
        new()
        {
            EventId = eventId, Code = code, Status = CouponStatus.Issued,
            IssuedToUserId = userId, IssuedAt = issuedAtUtc,
        };

    /// <summary>
    /// 미발급 쿠폰을 발급 처리한다. 이미 발급된 쿠폰이면 false 를 돌려준다.
    /// (Redis 스트림 소비는 at-least-once 이므로 같은 항목이 두 번 들어올 수 있다.)
    /// </summary>
    public bool TryMarkIssued(string userId, DateTime issuedAtUtc)
    {
        if (Status != CouponStatus.Unissued) return false;
        Status = CouponStatus.Issued;
        IssuedToUserId = userId;
        IssuedAt = issuedAtUtc;
        return true;
    }

    public void Revoke(DateTime nowUtc, string reason)
    {
        Status = CouponStatus.Revoked;
        RevokedAt = nowUtc;
        RevokeReason = reason;
    }
}

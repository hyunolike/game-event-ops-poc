namespace CouponOps.Domain;

/// <summary>
/// 발급 시도 이력. 성공·실패를 모두 남긴다.
/// Redis 가 source of truth 이고 이 테이블은 영속화 담당이므로,
/// <see cref="RequestedAt"/>(API 수신)과 <see cref="PersistedAt"/>(DB 적재)의 차이가 곧 적재 지연이다.
/// </summary>
public class IssuanceLog
{
    private IssuanceLog() { }

    public long Id { get; private set; }
    /// <summary>멱등 키. UNIQUE 제약이 at-least-once 재처리의 중복 행을 막는다.</summary>
    public Guid RequestId { get; private set; }
    public long EventId { get; private set; }
    public string UserId { get; private set; } = default!;
    public IssueResult Result { get; private set; }
    public long? CouponId { get; private set; }
    /// <summary>비정규화. 이력 조회에서 Coupons 조인을 없앤다.</summary>
    public string? CouponCode { get; private set; }
    public DateTime RequestedAt { get; private set; }
    public DateTime PersistedAt { get; private set; }
    /// <summary>비동기 적재 지연(ms) = PersistedAt - RequestedAt. 집계·인덱싱을 위해 계산해 둔다.</summary>
    public int PersistenceLagMs { get; private set; }
    /// <summary>"redis-lua" 또는 "db-lock". 4단계 비교 측정에서 두 경로를 구분한다.</summary>
    public string IssuePath { get; private set; } = default!;
    public string? FailureDetail { get; private set; }

    public static IssuanceLog Create(
        Guid requestId, long eventId, string userId, IssueResult result,
        string? couponCode, DateTime requestedAtUtc, DateTime persistedAtUtc,
        string issuePath, string? failureDetail = null) =>
        new()
        {
            RequestId = requestId, EventId = eventId, UserId = userId, Result = result,
            CouponCode = couponCode, RequestedAt = requestedAtUtc, PersistedAt = persistedAtUtc,
            PersistenceLagMs = (int)(persistedAtUtc - requestedAtUtc).TotalMilliseconds,
            IssuePath = issuePath, FailureDetail = failureDetail,
        };

    public void AttachCoupon(long couponId) => CouponId = couponId;
}

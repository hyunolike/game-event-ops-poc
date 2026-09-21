namespace CouponOps.Domain;

/// <summary>
/// 추첨 시도 이력. 성공·실패를 모두 남긴다.
/// </summary>
/// <remarks>
/// <b>이 테이블의 존재 이유는 "재현" 이다.</b>
/// <see cref="WeightVersionId"/> 와 <see cref="RandomValue"/> 두 개만 있으면 추첨을 그대로
/// 재계산해 검증할 수 있다(<c>DrawVerifier</c>). CS 클레임·내부 감사·규제 대응·버그 조사가
/// 전부 여기서 끝난다. 난수를 Lua 밖(앱)에서 뽑는 이유가 정확히 이것이다.
/// </remarks>
public class DrawLog
{
    private DrawLog() { }

    public long Id { get; private set; }
    /// <summary>멱등 키. UNIQUE 제약이 at-least-once 재처리의 중복 행을 막는다.</summary>
    public Guid RequestId { get; private set; }
    public long DrawEventId { get; private set; }
    public string UserId { get; private set; } = default!;
    public DrawResult Result { get; private set; }

    // ── 재현에 필요한 최소 집합 ──────────────────────────────────────────────
    /// <summary>이 추첨이 사용한 확률표. 운영자가 나중에 확률을 바꿔도 과거를 설명할 수 있다.</summary>
    public long? WeightVersionId { get; private set; }
    /// <summary>앱이 뽑아 스크립트에 넘긴 난수 원본.</summary>
    public long RandomValue { get; private set; }
    public int TotalWeight { get; private set; }
    /// <summary><c>RandomValue % TotalWeight</c>. 천장이 적용된 추첨은 -1.</summary>
    public int Roll { get; private set; }
    // ────────────────────────────────────────────────────────────────────────

    public long? PrizeId { get; private set; }
    /// <summary>대체가 적용된 경우 치환 전 경품. 재현 결과와 비교할 대상이 이쪽이다.</summary>
    public long? OriginalPrizeId { get; private set; }
    public bool FallbackApplied { get; private set; }
    public bool PityApplied { get; private set; }

    /// <summary>비정규화. 이력 조회에서 DrawPrizes 조인을 없앤다(경품명은 나중에 바뀔 수 있다).</summary>
    public string? PrizeName { get; private set; }
    public long? ItemId { get; private set; }
    public int ItemQty { get; private set; }

    public int TicketsSpent { get; private set; }
    public int PityCountAfter { get; private set; }

    /// <summary>
    /// 요청을 보낸 클라이언트 IP. 신뢰할 프록시가 설정된 배포에서만 의미가 있다
    /// (Application 계층의 <c>NetworkOptions</c> 참조).
    /// </summary>
    /// <remarks>
    /// 개인정보다. 이상 탐지(다계정 판별) 외의 용도로 쓰지 않고, 이력 보존 기간이 지나면 함께 사라진다.
    /// IPv6 최대 길이(45자)에 맞춘다.
    /// </remarks>
    public string? ClientIp { get; private set; }

    public DateTime RequestedAt { get; private set; }
    public DateTime PersistedAt { get; private set; }
    /// <summary>비동기 적재 지연(ms). Redis 가 source of truth 이고 DB 는 뒤따라온다.</summary>
    public int PersistenceLagMs { get; private set; }
    public string? FailureDetail { get; private set; }

    public static DrawLog Create(
        Guid requestId, long drawEventId, string userId, DrawResult result,
        long? weightVersionId, long randomValue, int totalWeight, int roll,
        long? prizeId, long? originalPrizeId, bool fallbackApplied, bool pityApplied,
        string? prizeName, long? itemId, int itemQty, int ticketsSpent, int pityCountAfter,
        DateTime requestedAtUtc, DateTime persistedAtUtc, string? clientIp = null,
        string? failureDetail = null) =>
        new()
        {
            RequestId = requestId, DrawEventId = drawEventId, UserId = userId, Result = result,
            WeightVersionId = weightVersionId, RandomValue = randomValue,
            TotalWeight = totalWeight, Roll = roll,
            PrizeId = prizeId, OriginalPrizeId = originalPrizeId,
            FallbackApplied = fallbackApplied, PityApplied = pityApplied,
            PrizeName = prizeName, ItemId = itemId, ItemQty = itemQty,
            TicketsSpent = ticketsSpent, PityCountAfter = pityCountAfter,
            ClientIp = clientIp,
            RequestedAt = requestedAtUtc, PersistedAt = persistedAtUtc,
            PersistenceLagMs = (int)(persistedAtUtc - requestedAtUtc).TotalMilliseconds,
            FailureDetail = failureDetail,
        };
}

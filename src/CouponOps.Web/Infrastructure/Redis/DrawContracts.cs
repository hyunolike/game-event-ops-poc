using CouponOps.Domain;

namespace CouponOps.Infrastructure.Redis;

/// <summary>
/// 추첨 한 건의 결과. 스크립트가 돌려준 값 그대로이며, 이름 해소(경품명)는 상위 계층이 한다.
/// </summary>
/// <param name="Roll"><c>RandomValue % TotalWeight</c>. 천장이 적용됐으면 -1.</param>
/// <param name="RemainingTickets">티켓을 쓰지 않는 이벤트면 -1.</param>
/// <param name="RemainingDraws">일일 제한이 없거나 멱등 재생이면 -1.</param>
/// <param name="PriorResult">멱등 재생일 때 최초 시도의 결과. 그 외에는 null.</param>
public readonly record struct DrawOutcome(
    DrawResult Result,
    long PrizeId,
    long ItemId,
    int ItemQty,
    int Roll,
    bool FallbackApplied,
    bool PityApplied,
    int PityCountAfter,
    int RemainingTickets,
    int RemainingDraws,
    DrawResult? PriorResult,
    long WeightVersionId,
    long OriginalPrizeId,
    int TotalWeight);

/// <summary>워밍업에 넘기는 슬롯 하나. 누적 가중치는 스토어가 계산한다.</summary>
public sealed record DrawPrizeSlot(
    long PrizeId, int Weight, long? FallbackPrizeId, bool IsBlank, long ItemId, int ItemQty, int? Stock);

/// <summary>스트림에서 읽어온 추첨 이력 한 건.</summary>
public sealed record DrawnEntry(
    string StreamId, Guid RequestId, string UserId, DrawResult Result,
    long PrizeId, long OriginalPrizeId, long ItemId, int ItemQty,
    int TicketsSpent, bool FallbackApplied, bool PityApplied, int PityCountAfter,
    long WeightVersionId, long RandomValue, int Roll, int TotalWeight,
    DateTime RequestedAtUtc);

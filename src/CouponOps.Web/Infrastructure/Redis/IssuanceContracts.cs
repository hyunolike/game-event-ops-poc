using CouponOps.Domain;

namespace CouponOps.Infrastructure.Redis;

/// <param name="Result">발급 결과.</param>
/// <param name="CouponCode">성공 또는 멱등 재생 시의 쿠폰 코드.</param>
/// <param name="Remaining">성공 시 잔여 수량. 알 수 없으면 -1.</param>
/// <param name="PriorResult">
/// <see cref="IssueResult.DuplicateRequest"/> 일 때 최초 시도의 결과. 그 외에는 null.
/// </param>
public readonly record struct IssueOutcome(
    IssueResult Result, string? CouponCode, int Remaining, IssueResult? PriorResult);

/// <summary>스트림에서 읽어온 발급 이력 한 건.</summary>
public sealed record IssuedEntry(
    string StreamId, Guid RequestId, string UserId, IssueResult Result,
    string? CouponCode, DateTime RequestedAtUtc);

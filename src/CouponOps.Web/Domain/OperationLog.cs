namespace CouponOps.Domain;

/// <summary>
/// 운영자의 모든 변경 액션 감사 로그. append-only.
/// 대상은 FK 가 아니라 (TargetType, TargetId) 문자열로 느슨하게 가리킨다 —
/// 감사 로그는 대상 테이블이 늘어날 때마다 스키마가 바뀌면 안 되고, 대상이 삭제돼도 남아야 한다.
/// </summary>
public class OperationLog
{
    private OperationLog() { }

    public long Id { get; private set; }
    public long ActorId { get; private set; }
    /// <summary>비정규화. 운영자 계정이 비활성/삭제돼도 "누가 바꿨는지"가 로그 안에서 자립한다.</summary>
    public string ActorLoginId { get; private set; } = default!;
    public OperationAction Action { get; private set; }
    public string TargetType { get; private set; } = default!;
    public string TargetId { get; private set; } = default!;
    public string? BeforeJson { get; private set; }
    public string? AfterJson { get; private set; }
    /// <summary>변경 필드 요약. 목록 화면이 매 행의 LOB 을 파싱하지 않도록 쓰기 시점에 계산해 둔다.</summary>
    public string? ChangedFields { get; private set; }
    public string? Reason { get; private set; }
    public string? ClientIp { get; private set; }
    public Guid CorrelationId { get; private set; }
    public DateTime OccurredAt { get; private set; }

    public static OperationLog Create(
        long actorId, string actorLoginId, OperationAction action,
        string targetType, string targetId, DateTime occurredAtUtc,
        string? beforeJson = null, string? afterJson = null, string? changedFields = null,
        string? reason = null, string? clientIp = null, Guid? correlationId = null) =>
        new()
        {
            ActorId = actorId, ActorLoginId = actorLoginId, Action = action,
            TargetType = targetType, TargetId = targetId, OccurredAt = occurredAtUtc,
            BeforeJson = beforeJson, AfterJson = afterJson, ChangedFields = changedFields,
            Reason = reason, ClientIp = clientIp, CorrelationId = correlationId ?? Guid.NewGuid(),
        };
}

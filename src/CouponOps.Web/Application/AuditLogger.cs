using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;

namespace CouponOps.Application;

/// <summary>운영자의 변경 액션을 감사 로그로 남긴다.</summary>
public interface IAuditLogger
{
    /// <remarks>
    /// <b>SaveChanges 는 호출자가 한다.</b> 변경과 감사 로그가 같은 트랜잭션에서 커밋돼야
    /// "바뀌었는데 기록이 없는" 상태가 생기지 않는다.
    /// </remarks>
    void Record(OperationAction action, string targetType, string targetId,
                object? before, object? after, string? reason = null);
}

public sealed class AuditLogger(
    AppDbContext db, ICurrentActor actor, IHttpContextAccessor accessor, TimeProvider clock) : IAuditLogger
{
    /// <remarks>
    /// 기본 인코더는 비 ASCII 를 \uXXXX 로 이스케이프한다. 감사 로그는 사람이 읽는 것이
    /// 존재 이유인데 한글 값이 전부 \uC2E0\uADDC 로 저장되면 쓸모가 없다.
    /// 범위를 넓혀도 &lt;, &gt;, &amp; 이스케이프는 유지되므로 HTML 에 렌더해도 안전하다
    /// (UnsafeRelaxedJsonEscaping 과 달리 이쪽은 HTML 민감 문자를 계속 막는다).
    /// </remarks>
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public void Record(OperationAction action, string targetType, string targetId,
                       object? before, object? after, string? reason = null)
    {
        var beforeJson = before is null ? null : JsonSerializer.Serialize(before, Json);
        var afterJson = after is null ? null : JsonSerializer.Serialize(after, Json);

        db.OperationLogs.Add(OperationLog.Create(
            actorId: actor.Id,
            actorLoginId: actor.LoginId,
            action: action,
            targetType: targetType,
            targetId: targetId,
            occurredAtUtc: clock.GetUtcNow().UtcDateTime,
            beforeJson: beforeJson,
            afterJson: afterJson,
            changedFields: DiffFieldNames(beforeJson, afterJson),
            reason: reason,
            clientIp: accessor.HttpContext?.Connection.RemoteIpAddress?.ToString()));
    }

    /// <summary>
    /// 바뀐 필드 이름만 뽑아 둔다.
    /// 목록 화면이 매 행의 Before/After(LOB)를 파싱하지 않도록 쓰기 시점에 한 번만 계산한다.
    /// </summary>
    private static string? DiffFieldNames(string? beforeJson, string? afterJson)
    {
        if (beforeJson is null || afterJson is null) return null;

        var before = JsonNode.Parse(beforeJson)?.AsObject();
        var after = JsonNode.Parse(afterJson)?.AsObject();
        if (before is null || after is null) return null;

        var changed = after
            .Where(kv => before[kv.Key]?.ToJsonString() != kv.Value?.ToJsonString())
            .Select(kv => kv.Key)
            .ToList();

        return changed.Count == 0 ? null : string.Join(", ", changed);
    }
}

/// <summary>감사 로그의 Before/After 로 저장되는 이벤트 스냅샷.</summary>
public sealed record EventSnapshot(
    string Name, DateTime StartsAt, DateTime EndsAt,
    int TotalQuantity, int PerUserLimit, string IssuanceMode, bool Suspended)
{
    public static EventSnapshot Of(CouponEvent e) => new(
        e.Name, e.StartsAt, e.EndsAt, e.TotalQuantity, e.PerUserLimit,
        e.IssuanceMode.ToString(), e.SuspendedAt is not null);
}

namespace CouponOps.Domain;

public enum ApprovalStatus : byte
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    /// <summary>요청자가 스스로 거둬들였다.</summary>
    Cancelled = 3,
}

/// <summary>
/// 진행 중 이벤트의 확률 변경 요청. 등록자와 다른 편집자의 승인이 있어야 적용된다.
/// </summary>
/// <remarks>
/// 왜 확률 변경만 2인 승인인가 — 이 액션은 <b>공시와 직결</b>되고,
/// 적용되는 순간 이미 돌아간 추첨과 앞으로 돌아갈 추첨의 확률이 달라진다.
/// 되돌릴 수는 있지만(새 버전 활성화), 그 사이에 나간 아이템은 회수되지 않는다.
/// 혼자 누를 수 있는 액션으로 두기에는 비용이 비대칭이다.
/// <para>
/// 반대로 시작 전 이벤트는 아직 아무도 뽑지 않았으므로 승인 없이 바꾼다 —
/// 절차를 불필요한 곳까지 늘리면 운영자는 절차를 우회할 방법을 찾는다.
/// </para>
/// </remarks>
public class DrawApproval
{
    private DrawApproval() { }

    public long Id { get; private set; }
    public long DrawEventId { get; private set; }

    /// <summary>요청한 가중치. {"slotIndex": weight} 직렬화.</summary>
    public string PayloadJson { get; private set; } = default!;

    /// <summary>
    /// 요청 시점의 활성 확률표. 승인 시점에 이것이 바뀌어 있으면 적용하지 않는다 —
    /// 승인자가 본 것과 다른 표 위에 변경을 얹게 되기 때문이다.
    /// </summary>
    public long? BaseWeightVersionId { get; private set; }

    public long RequestedByAdminId { get; private set; }
    /// <summary>비정규화. 계정이 비활성/삭제돼도 "누가 올렸는지" 가 기록 안에서 자립한다.</summary>
    public string RequestedByLoginId { get; private set; } = default!;
    public DateTime RequestedAt { get; private set; }
    public string RequestReason { get; private set; } = default!;

    public ApprovalStatus Status { get; private set; }

    public long? DecidedByAdminId { get; private set; }
    public string? DecidedByLoginId { get; private set; }
    public DateTime? DecidedAt { get; private set; }
    public string? DecisionNote { get; private set; }

    /// <summary>두 승인자가 동시에 눌러도 한 번만 적용되게 한다.</summary>
    public byte[] RowVersion { get; private set; } = default!;

    public static DrawApproval Create(
        long drawEventId, string payloadJson, long? baseWeightVersionId,
        long requestedByAdminId, string requestedByLoginId, DateTime nowUtc, string reason) =>
        new()
        {
            DrawEventId = drawEventId,
            PayloadJson = payloadJson,
            BaseWeightVersionId = baseWeightVersionId,
            RequestedByAdminId = requestedByAdminId,
            RequestedByLoginId = requestedByLoginId,
            RequestedAt = nowUtc,
            RequestReason = reason,
            Status = ApprovalStatus.Pending,
        };

    /// <summary>
    /// 이 요청을 결정할 수 있는 사람인가.
    /// </summary>
    /// <remarks>
    /// <b>등록자와 승인자가 달라야 한다</b> — 그것이 2인 승인의 전부다.
    /// 자기가 올린 것을 자기가 통과시킬 수 있으면 절차는 형식일 뿐이고,
    /// 사고가 났을 때 "두 사람이 봤다" 고 말할 수 없다.
    /// </remarks>
    public bool CanBeDecidedBy(long adminId) =>
        Status == ApprovalStatus.Pending && adminId != RequestedByAdminId;

    public bool TryApprove(long adminId, string loginId, DateTime nowUtc, string? note) =>
        TryDecide(ApprovalStatus.Approved, adminId, loginId, nowUtc, note);

    public bool TryReject(long adminId, string loginId, DateTime nowUtc, string? note) =>
        TryDecide(ApprovalStatus.Rejected, adminId, loginId, nowUtc, note);

    private bool TryDecide(
        ApprovalStatus decision, long adminId, string loginId, DateTime nowUtc, string? note)
    {
        if (!CanBeDecidedBy(adminId)) return false;

        Status = decision;
        DecidedByAdminId = adminId;
        DecidedByLoginId = loginId;
        DecidedAt = nowUtc;
        DecisionNote = note;
        return true;
    }

    /// <summary>요청자만 거둬들일 수 있다.</summary>
    public bool TryCancel(long adminId, string loginId, DateTime nowUtc)
    {
        if (Status != ApprovalStatus.Pending || adminId != RequestedByAdminId) return false;

        Status = ApprovalStatus.Cancelled;
        DecidedByAdminId = adminId;
        DecidedByLoginId = loginId;
        DecidedAt = nowUtc;
        return true;
    }
}

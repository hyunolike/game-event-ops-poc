using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CouponOps.Application;

public enum ApprovalOutcome
{
    /// <summary>시작 전 이벤트라 승인 없이 바로 적용했다.</summary>
    AppliedImmediately = 0,
    /// <summary>승인 대기에 들어갔다. 아직 확률은 바뀌지 않았다.</summary>
    PendingApproval = 1,
    Approved = 2,
    Rejected = 3,
    Cancelled = 4,
    /// <summary>등록자 본인이거나, 이미 결정된 요청이다.</summary>
    NotAllowed = 5,
    /// <summary>요청 이후 확률표가 바뀌었다. 승인자가 본 것과 다른 표 위에 얹게 된다.</summary>
    Stale = 6,
    NotFound = 7,
    /// <summary>다른 승인자가 한발 먼저 결정했다. 오류가 아니라 정상 경합의 결과다.</summary>
    AlreadyDecided = 8,
}

public sealed record ApprovalResult(ApprovalOutcome Outcome, long? ApprovalId = null);

public sealed record PendingApproval(
    long Id, long DrawEventId, string EventName, string EventCode,
    string RequestedByLoginId, DateTime RequestedAt, string Reason,
    IReadOnlyList<(int SlotIndex, string Name, int CurrentWeight, int RequestedWeight)> Changes);

/// <summary>
/// 확률 변경의 2인 승인(maker-checker).
/// </summary>
/// <remarks>
/// 절차를 <b>확률 변경에만</b> 건다. 모든 액션에 승인을 요구하면 운영자는 절차를 우회할 방법을
/// 찾게 되고, 그러면 정작 필요한 곳에서도 작동하지 않는다.
/// 확률 변경이 특별한 이유는 공시와 직결되고, 적용된 뒤 나간 아이템을 회수할 수 없기 때문이다.
/// </remarks>
public sealed class DrawApprovalService(
    AppDbContext db, DrawAdminService admin, IAuditLogger audit,
    ICurrentActor actor, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>
    /// 확률 변경 요청. 시작 전 이벤트는 즉시 적용하고, 시작된 이벤트는 승인 대기에 넣는다.
    /// </summary>
    /// <remarks>
    /// 시작 전이면 아직 아무도 뽑지 않았으므로 되돌릴 것이 없다 — 여기에 절차를 거는 것은
    /// 비용만 있고 얻는 것이 없다. 경계는 "한 번이라도 추첨이 가능했는가" 다.
    /// </remarks>
    public async Task<ApprovalResult> RequestWeightChangeAsync(
        long drawEventId, IReadOnlyDictionary<int, int> weightBySlot, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("확률 변경에는 사유가 필요합니다.");

        var ev = await db.DrawEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Id == drawEventId, ct);
        if (ev is null) return new ApprovalResult(ApprovalOutcome.NotFound);

        var now = clock.GetUtcNow().UtcDateTime;

        if (now < ev.StartsAt)
        {
            await admin.ChangeWeightsAsync(drawEventId, weightBySlot, reason, ct);
            return new ApprovalResult(ApprovalOutcome.AppliedImmediately);
        }

        var approval = DrawApproval.Create(
            drawEventId,
            JsonSerializer.Serialize(weightBySlot.ToDictionary(k => k.Key.ToString(), v => v.Value), Json),
            ev.ActiveWeightVersionId,
            actor.Id, actor.LoginId, now, reason.Trim());

        db.DrawApprovals.Add(approval);

        audit.Record(OperationAction.DrawWeightChangeRequested, nameof(DrawEvent), drawEventId.ToString(),
            before: null, after: new { Weights = weightBySlot }, reason);

        await db.SaveChangesAsync(ct);

        return new ApprovalResult(ApprovalOutcome.PendingApproval, approval.Id);
    }

    public async Task<ApprovalResult> ApproveAsync(long approvalId, string? note, CancellationToken ct)
    {
        var approval = await db.DrawApprovals.SingleOrDefaultAsync(a => a.Id == approvalId, ct);
        if (approval is null) return new ApprovalResult(ApprovalOutcome.NotFound);

        // 등록자와 승인자가 달라야 한다. 이 한 줄이 2인 승인의 전부다.
        if (!approval.CanBeDecidedBy(actor.Id))
            return new ApprovalResult(ApprovalOutcome.NotAllowed, approval.Id);

        var ev = await db.DrawEvents.AsNoTracking().SingleAsync(e => e.Id == approval.DrawEventId, ct);
        if (ev.ActiveWeightVersionId != approval.BaseWeightVersionId)
            // 요청 이후 다른 변경이 들어갔다. 승인자가 본 표와 지금 표가 다르므로 그대로 얹지 않는다.
            return new ApprovalResult(ApprovalOutcome.Stale, approval.Id);

        var now = clock.GetUtcNow().UtcDateTime;
        if (!approval.TryApprove(actor.Id, actor.LoginId, now, note))
            return new ApprovalResult(ApprovalOutcome.NotAllowed, approval.Id);

        var weights = Deserialize(approval.PayloadJson);

        audit.Record(OperationAction.DrawWeightChangeApproved, nameof(DrawEvent),
            approval.DrawEventId.ToString(),
            before: null,
            after: new { ApprovalId = approval.Id, approval.RequestedByLoginId, ApprovedBy = actor.LoginId },
            note ?? approval.RequestReason);

        // ── 순서에 대한 판단 ────────────────────────────────────────────────────
        // 승인 기록을 먼저 커밋하고, 그다음 실제 변경을 적용한다.
        //
        // 둘을 한 트랜잭션으로 묶고 싶어지지만 그렇게 하면 오히려 나빠진다 —
        // ChangeWeightsAsync 는 DB 를 커밋한 뒤 Redis 를 맞추는데, 바깥 트랜잭션이 있으면
        // Redis 쓰기가 DB 커밋보다 앞서게 되고, 커밋이 실패하면 판정의 원천(Redis)만
        // 새 확률로 앞서 나간다. 그것이 가장 나쁜 상태다.
        //
        // 남은 두 실패 모양 중에서는 이쪽을 택한다.
        //   (a) 승인됐는데 미적용  — 화면에서 확률표 버전이 그대로인 것이 보이고, 재요청으로 회복된다.
        //   (b) 적용됐는데 승인 기록 없음 — 누가 통과시켰는지 모르는 확률 변경이 남는다. 회복 불가.
        // 비대칭이므로 승인 기록이 먼저다.
        if (!await TryCommitDecisionAsync(approval, ct))
            return new ApprovalResult(ApprovalOutcome.AlreadyDecided, approval.Id);

        await admin.ChangeWeightsAsync(approval.DrawEventId, weights,
            $"{approval.RequestReason} (요청 {approval.RequestedByLoginId} · 승인 {actor.LoginId})", ct);

        return new ApprovalResult(ApprovalOutcome.Approved, approval.Id);
    }

    public async Task<ApprovalResult> RejectAsync(long approvalId, string? note, CancellationToken ct)
    {
        var approval = await db.DrawApprovals.SingleOrDefaultAsync(a => a.Id == approvalId, ct);
        if (approval is null) return new ApprovalResult(ApprovalOutcome.NotFound);

        var now = clock.GetUtcNow().UtcDateTime;
        if (!approval.TryReject(actor.Id, actor.LoginId, now, note))
            return new ApprovalResult(ApprovalOutcome.NotAllowed, approval.Id);

        audit.Record(OperationAction.DrawWeightChangeRejected, nameof(DrawEvent),
            approval.DrawEventId.ToString(),
            before: null,
            after: new { ApprovalId = approval.Id, approval.RequestedByLoginId, RejectedBy = actor.LoginId },
            note);

        return await TryCommitDecisionAsync(approval, ct)
            ? new ApprovalResult(ApprovalOutcome.Rejected, approval.Id)
            : new ApprovalResult(ApprovalOutcome.AlreadyDecided, approval.Id);
    }

    /// <summary>요청자가 스스로 거둬들인다.</summary>
    public async Task<ApprovalResult> CancelAsync(long approvalId, CancellationToken ct)
    {
        var approval = await db.DrawApprovals.SingleOrDefaultAsync(a => a.Id == approvalId, ct);
        if (approval is null) return new ApprovalResult(ApprovalOutcome.NotFound);

        if (!approval.TryCancel(actor.Id, actor.LoginId, clock.GetUtcNow().UtcDateTime))
            return new ApprovalResult(ApprovalOutcome.NotAllowed, approval.Id);

        audit.Record(OperationAction.DrawWeightChangeCancelled, nameof(DrawEvent),
            approval.DrawEventId.ToString(),
            before: null, after: new { ApprovalId = approval.Id, CancelledBy = actor.LoginId });

        return await TryCommitDecisionAsync(approval, ct)
            ? new ApprovalResult(ApprovalOutcome.Cancelled, approval.Id)
            : new ApprovalResult(ApprovalOutcome.AlreadyDecided, approval.Id);
    }

    /// <summary>
    /// 결정과 감사 기록을 함께 커밋한다. 다른 승인자가 한발 먼저 결정했으면 false 를 돌려준다.
    /// </summary>
    /// <remarks>
    /// <see cref="DrawApproval.RowVersion"/> 이 동시성 토큰이라, 둘이 같은 요청을 동시에 눌러도
    /// 한 쪽만 커밋된다. 진 쪽을 예외로 흘려보내면 운영자는 500 화면을 보게 되는데,
    /// 실제로 일어난 일은 "동료가 먼저 눌렀다" 일 뿐이다. 그 사실을 그대로 알려준다.
    /// </remarks>
    private async Task<bool> TryCommitDecisionAsync(DrawApproval approval, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // 더럽혀진 엔티티가 추적된 채 남으면 이후 저장이 같은 예외를 되풀이한다.
            db.Entry(approval).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<IReadOnlyList<PendingApproval>> ListPendingAsync(long? drawEventId, CancellationToken ct)
    {
        var query = db.DrawApprovals.AsNoTracking().Where(a => a.Status == ApprovalStatus.Pending);
        if (drawEventId is { } id) query = query.Where(a => a.DrawEventId == id);

        var approvals = await query.OrderBy(a => a.RequestedAt).Take(100).ToListAsync(ct);
        if (approvals.Count == 0) return [];

        var eventIds = approvals.Select(a => a.DrawEventId).Distinct().ToList();

        var events = await db.DrawEvents.AsNoTracking()
            .Where(e => eventIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, ct);

        var prizes = (await db.DrawPrizes.AsNoTracking()
                .Where(p => eventIds.Contains(p.DrawEventId)).ToListAsync(ct))
            .GroupBy(p => p.DrawEventId)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.SlotIndex).ToList());

        return approvals.Select(a =>
        {
            var requested = Deserialize(a.PayloadJson);
            var slots = prizes.GetValueOrDefault(a.DrawEventId) ?? [];
            var ev = events.GetValueOrDefault(a.DrawEventId);

            // 바뀌는 슬롯만 보여준다. 스무 줄 중 한 줄이 달라졌을 때 그 한 줄을 눈으로 찾게 하면
            // 승인은 형식이 된다.
            var changes = slots
                .Where(p => requested.TryGetValue(p.SlotIndex, out var w) && w != p.Weight)
                .Select(p => (p.SlotIndex, p.Name, p.Weight, requested[p.SlotIndex]))
                .ToList();

            return new PendingApproval(
                a.Id, a.DrawEventId, ev?.Name ?? "(삭제됨)", ev?.Code ?? "",
                a.RequestedByLoginId, a.RequestedAt, a.RequestReason, changes);
        }).ToList();
    }

    private static Dictionary<int, int> Deserialize(string payloadJson) =>
        (JsonSerializer.Deserialize<Dictionary<string, int>>(payloadJson, Json) ?? [])
        .ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
}

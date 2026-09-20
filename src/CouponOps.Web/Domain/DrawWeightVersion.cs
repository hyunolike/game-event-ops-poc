namespace CouponOps.Domain;

/// <summary>
/// 가중치(확률) 테이블의 스냅샷. append-only.
/// </summary>
/// <remarks>
/// <b>진행 중인 이벤트의 확률을 바꾸면 그전에 일어난 추첨을 설명할 수 없게 된다.</b>
/// 그래서 가중치 변경은 UPDATE 가 아니라 새 버전 INSERT + 활성 버전 전환이다.
/// <see cref="DrawLog.WeightVersionId"/> 가 자기가 쓴 버전을 가리키므로,
/// 6개월 뒤의 CS 문의에도 그 시점의 확률표를 정확히 꺼낼 수 있다.
/// </remarks>
public class DrawWeightVersion
{
    private DrawWeightVersion() { }

    public long Id { get; private set; }
    public long DrawEventId { get; private set; }
    public int Version { get; private set; }

    /// <summary>[{prizeId, slotIndex, name, weight, stock, itemId, qty, isBlank, fallbackPrizeId}, ...]</summary>
    public string SnapshotJson { get; private set; } = default!;

    /// <summary>스냅샷의 SHA-256(hex). 저장된 확률표의 위변조를 탐지한다.</summary>
    public string SnapshotHash { get; private set; } = default!;

    public int TotalWeight { get; private set; }
    public DateTime ActivatedAt { get; private set; }
    public DateTime? DeactivatedAt { get; private set; }
    public long CreatedByAdminId { get; private set; }
    /// <summary>변경 사유. 확률 변경은 사유 없이 남으면 사후 조사에서 아무것도 설명하지 못한다.</summary>
    public string ChangeReason { get; private set; } = default!;

    public static DrawWeightVersion Create(
        long drawEventId, int version, string snapshotJson, string snapshotHash,
        int totalWeight, DateTime activatedAtUtc, long createdByAdminId, string changeReason) =>
        new()
        {
            DrawEventId = drawEventId, Version = version,
            SnapshotJson = snapshotJson, SnapshotHash = snapshotHash, TotalWeight = totalWeight,
            ActivatedAt = activatedAtUtc, CreatedByAdminId = createdByAdminId, ChangeReason = changeReason,
        };

    public void Deactivate(DateTime nowUtc) => DeactivatedAt = nowUtc;
}

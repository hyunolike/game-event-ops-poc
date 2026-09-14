using CouponOps.Domain;

namespace CouponOps.Infrastructure.Redis;

public interface IIssuanceStore
{
    /// <summary>재고 차감·한도 검사·멱등 처리를 원자적으로 수행한다.</summary>
    Task<IssueOutcome> IssueAsync(
        long eventId, string userId, Guid requestId, DateTime nowUtc, bool logFailure, CancellationToken ct);

    /// <summary>이벤트 메타와 재고를 Redis 에 적재한다(오픈 전 워밍업).</summary>
    Task WarmAsync(CouponEvent ev, IReadOnlyList<string> preGeneratedCodes, CancellationToken ct);

    /// <summary>운영자 강제 중단/재개를 Redis 메타에 즉시 반영한다.</summary>
    Task SetSuspendedAsync(long eventId, bool suspended, CancellationToken ct);

    /// <summary>현재 잔여 수량.</summary>
    Task<long> GetRemainingAsync(long eventId, IssuanceMode mode, CancellationToken ct);

    /// <summary>적재 워커가 폴링해야 할 이벤트 목록.</summary>
    Task<IReadOnlyList<long>> GetRegisteredEventIdsAsync(CancellationToken ct);

    /// <summary>스트림에서 미적재 이력을 읽는다(소비자 그룹).</summary>
    /// <param name="reclaimOwn">
    /// true 면 이 소비자가 읽었지만 아직 확인(ACK)하지 않은 항목을 다시 읽는다.
    /// 워커가 적재 도중 죽으면 해당 항목은 PEL 에 남아 '>' 로는 다시 오지 않으므로,
    /// 재기동 시 이 경로로 회수한다.
    /// </param>
    Task<IReadOnlyList<IssuedEntry>> ReadPendingAsync(
        long eventId, string consumer, int count, bool reclaimOwn, CancellationToken ct);

    /// <summary>DB 적재가 끝난 항목을 확인 처리한다.</summary>
    Task AcknowledgeAsync(long eventId, IReadOnlyList<string> streamIds, CancellationToken ct);
}

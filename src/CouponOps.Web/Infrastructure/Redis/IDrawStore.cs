using CouponOps.Domain;

namespace CouponOps.Infrastructure.Redis;

public interface IDrawStore
{
    /// <summary>
    /// 추첨 한 건. 슬롯 결정·재고 차감·티켓 차감·천장·일일 한도·멱등을 원자적으로 수행한다.
    /// </summary>
    /// <param name="randomValue">
    /// 앱이 뽑아 넘기는 난수. <b>스크립트 안에서 뽑지 않는다</b> —
    /// 이 값을 이력에 남겨야 사후에 추첨을 그대로 재계산할 수 있다.
    /// </param>
    /// <param name="dayKey">
    /// 일일 카운터의 날짜 구분자. 리셋 시각·타임존 계산은 앱이 하고 결과만 키 이름으로 넘긴다.
    /// </param>
    /// <param name="clientIp">
    /// 이상 탐지(다계정 판별)용. 판정에는 쓰이지 않고 이력에만 실린다.
    /// 신뢰할 프록시가 설정되지 않은 배포에서는 의미가 없으므로 탐지 쪽에서 걸러 쓴다.
    /// </param>
    Task<DrawOutcome> SpinAsync(
        long drawEventId, string userId, Guid requestId, DateTime nowUtc,
        long randomValue, string dayKey, bool logFailure, string? clientIp, CancellationToken ct);

    /// <summary>이벤트 메타·누적 가중치·재고를 Redis 에 적재한다(오픈 전 워밍업).</summary>
    Task WarmAsync(DrawEvent ev, IReadOnlyList<DrawPrizeSlot> slots, CancellationToken ct);

    /// <summary>
    /// 누적 가중치 배열과 메타만 다시 쓴다(확률 변경).
    /// </summary>
    /// <remarks>
    /// <b>재고·천장 카운터·티켓 잔액·당첨 누적은 건드리지 않는다.</b>
    /// 워밍업으로 대신하면 확률을 한 번 바꿀 때마다 모든 유저의 천장 진행도가 0 으로 돌아가고
    /// 소진된 재고가 되살아난다 — 둘 다 유저에게 직접 피해가 가는 사고다.
    /// </remarks>
    Task RewriteWeightsAsync(DrawEvent ev, IReadOnlyList<DrawPrizeSlot> slots, CancellationToken ct);

    /// <summary>
    /// 메타(기간·한도·천장·활성 가중치 버전)만 다시 반영한다.
    /// 재고·천장 카운터·티켓 잔액은 건드리지 않는다 — 진행 중 수정에서 초기화되면 초과 지급이 된다.
    /// </summary>
    Task RefreshMetaAsync(DrawEvent ev, int totalWeight, CancellationToken ct);

    Task SetSuspendedAsync(long drawEventId, bool suspended, CancellationToken ct);

    /// <summary>경품별 잔여 재고. 무제한 슬롯은 결과에 포함되지 않는다.</summary>
    Task<IReadOnlyDictionary<long, int>> GetStockAsync(long drawEventId, CancellationToken ct);

    /// <summary>경품별 당첨 누적 수. 기대값 대비 편차 모니터링이 읽는다.</summary>
    Task<IReadOnlyDictionary<long, long>> GetWinCountsAsync(long drawEventId, CancellationToken ct);

    Task<int> GetTicketsAsync(long drawEventId, string userId, CancellationToken ct);

    /// <summary>티켓 지급. 운영자 액션이거나 게임 서버가 호출한다.</summary>
    Task<int> GrantTicketsAsync(long drawEventId, string userId, int amount, CancellationToken ct);

    Task<int> GetDrawsTodayAsync(long drawEventId, string userId, string dayKey, CancellationToken ct);

    Task<IReadOnlyList<long>> GetRegisteredEventIdsAsync(CancellationToken ct);

    /// <summary>스트림에서 미적재 이력을 읽는다(소비자 그룹).</summary>
    /// <param name="reclaimOwn">
    /// true 면 이 소비자가 읽었지만 확인(ACK)하지 않은 항목을 다시 읽는다.
    /// 워커가 적재 도중 죽으면 해당 항목은 '&gt;' 로 다시 오지 않으므로 재기동 시 이 경로로 회수한다.
    /// </param>
    Task<IReadOnlyList<DrawnEntry>> ReadPendingAsync(
        long drawEventId, string consumer, int count, bool reclaimOwn, CancellationToken ct);

    Task AcknowledgeAsync(long drawEventId, IReadOnlyList<string> streamIds, CancellationToken ct);
}

namespace CouponOps.Domain;

/// <summary>
/// 기간과 중단 여부로부터 이벤트 상태를 파생하는 규칙.
/// </summary>
/// <remarks>
/// 쿠폰 이벤트와 룰렛 이벤트가 같은 규칙을 쓴다. 각 엔티티에 복사해 두면
/// 한쪽만 고치는 사고가 나므로 스칼라 규칙은 여기 한 곳에만 둔다.
/// (SQL 술어는 엔티티 타입별 식 트리라 공유할 수 없어 각 엔티티의 <c>IsStatus</c> 에 남는다 —
///  그 둘을 바꿀 때는 반드시 이 함수와 함께 바꾼다.)
/// </remarks>
public static class EventSchedule
{
    public static EventStatus StatusAt(DateTime? suspendedAt, DateTime startsAt, DateTime endsAt, DateTime utcNow) =>
        suspendedAt is not null ? EventStatus.Suspended
        : utcNow < startsAt     ? EventStatus.Scheduled
        : utcNow >= endsAt      ? EventStatus.Ended
                                : EventStatus.Active;
}

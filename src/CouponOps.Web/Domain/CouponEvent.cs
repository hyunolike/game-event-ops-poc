using System.Linq.Expressions;

namespace CouponOps.Domain;

/// <summary>
/// 선착순 쿠폰 발급 이벤트.
/// </summary>
/// <remarks>
/// 상태(예정/진행중/종료/중단) 중 저장하는 것은 운영자 액션인 <see cref="SuspendedAt"/> 뿐이다.
/// 나머지 셋은 현재 시각의 함수이므로 저장하면 스케줄러가 밀리는 순간 DB에 거짓 상태가 남는다.
/// 설계 근거는 docs/01-domain-design.md 2장 참조.
/// </remarks>
public class CouponEvent
{
    private CouponEvent() { }  // EF Core 용

    public long Id { get; private set; }
    public string Code { get; private set; } = default!;
    public string Name { get; private set; } = default!;

    public DateTime StartsAt { get; private set; }       // UTC
    public DateTime EndsAt { get; private set; }         // UTC
    public int TotalQuantity { get; private set; }
    public int PerUserLimit { get; private set; }
    public IssuanceMode IssuanceMode { get; private set; }

    public DateTime? SuspendedAt { get; private set; }
    public string? SuspendReason { get; private set; }
    public DateTime? PoolWarmedAt { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = default!;

    public static CouponEvent Create(
        string code, string name, DateTime startsAtUtc, DateTime endsAtUtc,
        int totalQuantity, int perUserLimit, IssuanceMode mode, DateTime nowUtc)
    {
        var e = new CouponEvent
        {
            Code = code,
            Name = name,
            StartsAt = startsAtUtc,
            EndsAt = endsAtUtc,
            TotalQuantity = totalQuantity,
            PerUserLimit = perUserLimit,
            IssuanceMode = mode,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
        };
        var errors = e.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join("; ", errors));
        return e;
    }

    public IReadOnlyList<string> Validate() =>
        ValidateInput(Code, Name, StartsAt, EndsAt, TotalQuantity, PerUserLimit);

    /// <summary>
    /// 입력값 검증. 엔티티를 건드리기 전에 호출할 수 있도록 static 으로 둔다 —
    /// 먼저 바꾸고 나중에 검증하면 실패 시 EF 가 추적 중인 엔티티가 더럽혀진 채 남는다.
    /// </summary>
    public static IReadOnlyList<string> ValidateInput(
        string code, string name, DateTime startsAt, DateTime endsAt, int totalQuantity, int perUserLimit)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(code)) errors.Add("이벤트 코드는 필수입니다.");
        if (string.IsNullOrWhiteSpace(name)) errors.Add("이벤트 이름은 필수입니다.");
        if (endsAt <= startsAt) errors.Add("종료 시각은 시작 시각보다 뒤여야 합니다.");
        if (totalQuantity <= 0) errors.Add("총 발급 수량은 1 이상이어야 합니다.");
        if (perUserLimit <= 0) errors.Add("유저당 발급 한도는 1 이상이어야 합니다.");
        if (perUserLimit > totalQuantity) errors.Add("유저당 발급 한도가 총 수량을 넘을 수 없습니다.");
        return errors;
    }

    /// <summary>운영자 수정. 코드(슬러그)는 외부에 노출된 식별자이므로 바꾸지 않는다.</summary>
    public void Update(string name, DateTime startsAtUtc, DateTime endsAtUtc,
                       int totalQuantity, int perUserLimit, DateTime nowUtc)
    {
        var errors = ValidateInput(Code, name, startsAtUtc, endsAtUtc, totalQuantity, perUserLimit);
        if (errors.Count > 0) throw new ArgumentException(string.Join("; ", errors));

        Name = name;
        StartsAt = startsAtUtc;
        EndsAt = endsAtUtc;
        TotalQuantity = totalQuantity;
        PerUserLimit = perUserLimit;
        UpdatedAt = nowUtc;
    }

    /// <summary>저장된 값과 현재 시각으로부터 상태를 파생한다.</summary>
    public EventStatus StatusAt(DateTime utcNow) =>
        EventSchedule.StatusAt(SuspendedAt, StartsAt, EndsAt, utcNow);

    /// <summary>
    /// <see cref="StatusAt"/> 와 동일한 규칙을 SQL 로 번역 가능한 술어로 표현한다.
    /// 운영툴 목록의 상태 필터가 메모리 필터링이 아니라 서버 측 WHERE 절이 되고,
    /// (SuspendedAt, StartsAt) 인덱스를 탄다.
    /// 규칙이 두 곳에 있으므로, 바꿀 때는 반드시 <see cref="StatusAt"/> 도 함께 바꾼다.
    /// </summary>
    public static Expression<Func<CouponEvent, bool>> IsStatus(EventStatus status, DateTime utcNow) =>
        status switch
        {
            EventStatus.Suspended => e => e.SuspendedAt != null,
            EventStatus.Scheduled => e => e.SuspendedAt == null && utcNow < e.StartsAt,
            EventStatus.Ended     => e => e.SuspendedAt == null && utcNow >= e.EndsAt,
            _                     => e => e.SuspendedAt == null && utcNow >= e.StartsAt && utcNow < e.EndsAt,
        };

    public void Suspend(DateTime nowUtc, string reason)
    {
        SuspendedAt = nowUtc;
        SuspendReason = reason;
        UpdatedAt = nowUtc;
    }

    public void Resume(DateTime nowUtc)
    {
        SuspendedAt = null;
        SuspendReason = null;
        UpdatedAt = nowUtc;
    }

    public void MarkPoolWarmed(DateTime nowUtc)
    {
        PoolWarmedAt = nowUtc;
        UpdatedAt = nowUtc;
    }
}

namespace CouponOps.Infrastructure.Redis;

public sealed class IssuanceOptions
{
    public const string SectionName = "Issuance";

    /// <summary>파생(Derived) 모드 쿠폰 코드의 해시 접미사를 만드는 비밀값.</summary>
    public string CodeSecret { get; set; } = "change-me-in-production";

    /// <summary>
    /// 멱등 키 보관 기간. 이 기간 안의 재시도는 최초 결과를 그대로 돌려받는다.
    /// 이벤트 기간보다 짧으면 늦은 재시도가 재평가되므로, 실제 운영에서는 이벤트 길이 이상으로 잡는다.
    /// </summary>
    public TimeSpan IdempotencyTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// 실패 이력을 스트림에 남길 비율(0.0 ~ 1.0).
    /// 수량 소진 후에는 실패가 성공의 수십 배가 되므로, 부하 상황에서 낮춰 스트림 폭주를 막는다.
    /// </summary>
    public double FailureLogSampleRate { get; set; } = 1.0;

    /// <summary>적재 워커가 한 번에 읽는 스트림 항목 수. 2100 파라미터 제한을 고려해 500 이하로 둔다.</summary>
    public int PersistenceBatchSize { get; set; } = 500;

    /// <summary>적재 워커 폴링 주기.</summary>
    public TimeSpan PersistencePollInterval { get; set; } = TimeSpan.FromMilliseconds(200);
}

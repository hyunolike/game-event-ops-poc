namespace CouponOps.Application;

/// <summary>
/// 클라이언트 IP 를 어디까지 믿을 것인가.
/// </summary>
/// <remarks>
/// 프록시 뒤에서 <c>X-Forwarded-For</c> 를 검증 없이 믿으면 누구나 자기 IP 를 위조할 수 있다.
/// 그러면 이상 탐지의 IP 축은 탐지 도구가 아니라 <b>남에게 혐의를 씌우는 도구</b>가 된다.
/// 그래서 신뢰할 프록시를 명시적으로 적은 경우에만 헤더를 해석한다.
/// <para>
/// 반대로 아무 설정도 없으면 프록시 뒤에서는 모든 요청이 프록시 IP 하나로 보인다.
/// 그 상태의 IP 축은 전원을 이상 징후로 띄우는 오탐 장치이므로 아예 돌리지 않는다 —
/// <see cref="EffectiveClientIpTrusted"/> 참조.
/// </para>
/// </remarks>
public sealed class NetworkOptions
{
    public const string SectionName = "Network";

    /// <summary>신뢰할 프록시의 IP 목록.</summary>
    public string[] TrustedProxies { get; set; } = [];

    /// <summary>신뢰할 프록시 대역(CIDR). 컨테이너 IP 는 고정이 아니므로 보통 이쪽을 쓴다.</summary>
    public string[] TrustedProxyNetworks { get; set; } = [];

    /// <summary>
    /// 프록시 없이 직접 노출된 배포에서 IP 를 믿겠다고 명시할 때 true.
    /// 지정하지 않으면 "신뢰 프록시가 설정돼 있는가" 로 판단한다.
    /// </summary>
    public bool? ClientIpTrusted { get; set; }

    public bool IsBehindTrustedProxy =>
        TrustedProxies.Length > 0 || TrustedProxyNetworks.Length > 0;

    /// <summary>이 값이 false 면 이상 탐지의 IP 축을 돌리지 않는다.</summary>
    public bool EffectiveClientIpTrusted => ClientIpTrusted ?? IsBehindTrustedProxy;
}

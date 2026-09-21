namespace CouponOps.Domain;

/// <summary>
/// 추첨 시도의 결과. 실패 사유를 구분해 이력에 남긴다.
/// </summary>
/// <remarks>
/// 3~6 은 <see cref="IssueResult"/> 와 같은 의미·같은 값이다. 운영툴의 결과 필터가
/// 두 이벤트 종류에서 같은 어휘를 쓰게 하기 위함이다.
/// <para>
/// <see cref="Won"/> 에는 꽝도 포함된다. "추첨이 정상 수행됐다" 는 뜻이고,
/// 무엇을 받았는지는 <see cref="DrawLog.PrizeId"/> 가 말한다.
/// 꽝을 실패로 분류하면 실패율 지표가 이벤트 설계(꽝 비중)에 따라 출렁여 쓸모가 없어진다.
/// </para>
/// </remarks>
public enum DrawResult : byte
{
    Won = 1,
    OutOfPeriod = 3,
    DailyLimitExceeded = 4,
    DuplicateRequest = 5,
    Suspended = 6,
    /// <summary>티켓 잔액 부족.</summary>
    InsufficientTicket = 7,
    /// <summary>뽑힌 경품도, 대체 경품도 소진됐다. 티켓은 차감되지 않는다.</summary>
    AllPrizesSoldOut = 8,
    SystemError = 99,
}

/// <summary>재고가 소진된 경품이 뽑혔을 때의 처리. 설계 근거는 docs/04-roulette-design.md 1장.</summary>
public enum SoldOutPolicy : byte
{
    /// <summary>
    /// 지정된 대체 경품으로 치환한다. 공시 확률과 실행 확률이 항상 같다.
    /// 누적 가중치가 이벤트 기간 내내 불변이라 워밍업 때 한 번 만들면 된다.
    /// </summary>
    Fallback = 0,

    /// <summary>
    /// 소진 슬롯을 빼고 남은 가중치로 재산정한다.
    /// <b>공시 확률이 실행 확률과 달라지는 구간이 생기므로</b> 이 PoC 는 지원하지 않는다.
    /// 채택하려면 공시 페이지가 실시간 재고를 반영해야 한다(설계 문서 6장).
    /// </summary>
    Renormalize = 1,
}

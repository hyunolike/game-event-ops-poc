namespace CouponOps.Application;

/// <summary>
/// 일일 카운터의 "하루" 경계를 정한다.
/// </summary>
/// <remarks>
/// 게임 이벤트의 하루는 자정이 아니라 새벽(보통 04~06시)에 바뀐다.
/// 자정 리셋이면 밤늦게 접속한 유저가 "어제 몫" 을 쓰지 못하고 날린다.
/// <para>
/// 계산을 Lua 가 아니라 앱에서 하는 이유는, 스크립트 안에 타임존과 서머타임을 들이지 않기 위해서다.
/// 앱이 결과(날짜 문자열)만 키 이름으로 넘기면 스크립트는 그냥 해시 하나를 만질 뿐이다.
/// </para>
/// </remarks>
public static class DrawDay
{
    /// <summary>
    /// 리셋 시각 기준의 "오늘" 을 yyyyMMdd 로 돌려준다.
    /// 리셋 04:00 일 때 KST 03:59 는 전날, 04:00 은 당일이다.
    /// </summary>
    public static string For(DateTime utcNow, TimeSpan resetAt) =>
        KoreaTime.FromUtc(utcNow).Add(-resetAt).ToString("yyyyMMdd");

    /// <summary>다음 리셋까지 남은 시간. 운영툴과 클라이언트가 "N시간 뒤 초기화" 를 표시할 때 쓴다.</summary>
    public static TimeSpan UntilNextReset(DateTime utcNow, TimeSpan resetAt)
    {
        var kstNow = KoreaTime.FromUtc(utcNow);
        var todayReset = kstNow.Date.Add(resetAt);
        var next = kstNow < todayReset ? todayReset : todayReset.AddDays(1);
        return next - kstNow;
    }
}

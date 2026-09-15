namespace CouponOps.Application;

/// <summary>
/// 저장은 UTC, 표시는 KST. 변환은 이 한 곳에서만 한다.
/// </summary>
/// <remarks>
/// 이벤트 오픈 시각 버그의 단골 원인이 "어디선가 로컬 시각으로 저장된 값" 이다.
/// 경계를 한 곳으로 몰아 두면 그런 값이 섞여 들어올 자리가 없다.
/// </remarks>
public static class KoreaTime
{
    public static readonly TimeZoneInfo Zone = Resolve();

    private static TimeZoneInfo Resolve()
    {
        // 최소 컨테이너에는 tzdata 가 없을 수 있다. 한국은 서머타임이 없으므로 고정 +9 로 대체해도
        // 결과가 같다.
        foreach (var id in (string[])["Asia/Seoul", "Korea Standard Time"])
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("KST", TimeSpan.FromHours(9), "KST", "KST");
    }

    public static DateTime FromUtc(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    public static DateTime ToUtc(DateTime kst) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(kst, DateTimeKind.Unspecified), Zone);

    public static string Format(DateTime utc) => FromUtc(utc).ToString("yyyy-MM-dd HH:mm:ss");
    public static string FormatShort(DateTime utc) => FromUtc(utc).ToString("MM-dd HH:mm");
    public static string? Format(DateTime? utc) => utc is null ? null : Format(utc.Value);

    /// <summary>datetime-local 입력값 형식.</summary>
    public static string ToInput(DateTime utc) => FromUtc(utc).ToString("yyyy-MM-ddTHH:mm");
}

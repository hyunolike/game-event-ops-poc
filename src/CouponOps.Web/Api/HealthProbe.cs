namespace CouponOps.Api;

/// <summary>
/// 컨테이너 HEALTHCHECK 진입점. <c>dotnet CouponOps.Web.dll --healthcheck [url]</c>
/// </summary>
/// <remarks>
/// 이미지에 curl 이나 wget 을 넣지 않기 위해 앱 자신이 프로브 역할을 한다.
/// 패키지를 추가하면 이미지가 커지고 빌드가 외부 apt 저장소에 의존하게 된다 —
/// 폐쇄망이나 제한된 네트워크에서 빌드가 막히는 흔한 원인이다.
/// 대가는 검사마다 .NET 프로세스가 하나 뜨는 것인데, 10초 간격이면 무시할 만하다.
/// </remarks>
public static class HealthProbe
{
    public static async Task<int> RunAsync(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        try
        {
            var response = await http.GetAsync(url);
            if (response.IsSuccessStatusCode) return 0;

            await Console.Error.WriteLineAsync($"헬스체크 실패: {(int)response.StatusCode} {url}");
            return 1;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"헬스체크 실패: {ex.Message} {url}");
            return 1;
        }
    }
}

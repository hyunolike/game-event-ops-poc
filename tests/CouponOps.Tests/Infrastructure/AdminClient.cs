using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CouponOps.Tests.Infrastructure;

/// <summary>
/// 운영툴(Razor Pages)을 실제 브라우저처럼 다룬다 — 쿠키를 들고 다니고,
/// 폼 POST 전에 해당 페이지에서 안티포저리 토큰을 받아 온다.
/// </summary>
public sealed class AdminClient
{
    public const string SeedPassword = "test-admin-pw";

    private static readonly Regex TokenPattern = new(
        """name="__RequestVerificationToken"[^>]*value="([^"]+)""", RegexOptions.Compiled);

    private readonly HttpClient _http;

    public AdminClient(WebApplicationFactory<Program> app) =>
        // 리다이렉트를 따라가지 않아야 "로그인으로 튕겼는지" 를 확인할 수 있다.
        _http = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

    public HttpClient Http => _http;

    public Task<HttpResponseMessage> GetAsync(string url) => _http.GetAsync(url);

    public async Task<string> GetStringAsync(string url)
    {
        var res = await _http.GetAsync(url);
        return await res.Content.ReadAsStringAsync();
    }

    public async Task<HttpResponseMessage> LoginAsync(string loginId, string password = SeedPassword) =>
        await PostFormAsync("/Account/Login", "/Account/Login", new()
        {
            ["LoginId"] = loginId,
            ["Password"] = password,
        });

    /// <param name="tokenSourceUrl">안티포저리 토큰을 받아올 GET 주소.</param>
    /// <param name="postUrl">실제로 POST 할 주소(핸들러 포함).</param>
    public async Task<HttpResponseMessage> PostFormAsync(
        string tokenSourceUrl, string postUrl, Dictionary<string, string> fields)
    {
        var page = await _http.GetAsync(tokenSourceUrl);
        var html = await page.Content.ReadAsStringAsync();

        var match = TokenPattern.Match(html);
        if (!match.Success)
            throw new InvalidOperationException(
                $"{tokenSourceUrl} 에서 안티포저리 토큰을 찾지 못했습니다 (status {(int)page.StatusCode}).");

        fields["__RequestVerificationToken"] = match.Groups[1].Value;
        return await _http.PostAsync(postUrl, new FormUrlEncodedContent(fields));
    }

    /// <summary>
    /// 리다이렉트 대상을 경로+쿼리로 정규화한다.
    /// 쿠키 인증 핸들러는 절대 URL(http://localhost/...)로 Location 을 내려주는데,
    /// 테스트가 관심 있는 것은 호스트가 아니라 "어디로 보냈는가" 다.
    /// </summary>
    public static string? RedirectTarget(HttpResponseMessage res)
    {
        if (res.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther))
            return null;

        var location = res.Headers.Location;
        if (location is null) return null;

        return location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
    }
}

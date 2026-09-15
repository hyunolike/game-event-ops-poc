using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CouponOps.Tests.Infrastructure;

public sealed record IssueApiResponse(
    string Result, string? CouponCode, int Remaining, string? PriorResult, string RequestId, int LatencyMs);

public sealed record IssueCall(HttpStatusCode Status, IssueApiResponse? Body)
{
    public string Result => Body?.Result ?? $"HTTP {(int)Status}";
}

/// <summary>발급 경로. 4단계에서 세 경로를 같은 조건으로 비교한다.</summary>
public enum IssuePath
{
    /// <summary>운영 경로 — Redis Lua.</summary>
    RedisLua,
    /// <summary>대조군 — 이벤트 행 배타 락.</summary>
    DbLock,
    /// <summary>대조군 — 쿠폰 행 READPAST.</summary>
    DbSkipLocked,
}

public sealed class IssueClient(WebApplicationFactory<Program> app, IssuePath path = IssuePath.RedisLua)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http = app.CreateClient();

    private string Endpoint(long eventId) => path switch
    {
        IssuePath.DbLock => $"/api/events/{eventId}/coupons/issue-db",
        IssuePath.DbSkipLocked => $"/api/events/{eventId}/coupons/issue-db-skiplocked",
        _ => $"/api/events/{eventId}/coupons/issue",
    };

    public async Task<IssueCall> IssueAsync(long eventId, string userId, Guid? requestId = null)
    {
        var response = await _http.PostAsJsonAsync(Endpoint(eventId), new { userId, requestId });
        var raw = await response.Content.ReadAsStringAsync();

        // 본문이 JSON 이 아니면(처리되지 않은 예외 페이지 등) 그 사실이 보이도록 그대로 드러낸다.
        // 조용히 삼키면 "JSON 파싱 실패" 라는 무의미한 오류만 남는다.
        if (!raw.StartsWith('{'))
            throw new InvalidOperationException(
                $"{Endpoint(eventId)} 가 JSON 이 아닌 응답을 돌려줬습니다 "
                + $"(status {(int)response.StatusCode}): {raw[..Math.Min(400, raw.Length)]}");

        return new IssueCall(response.StatusCode, JsonSerializer.Deserialize<IssueApiResponse>(raw, Json));
    }

    /// <summary>
    /// 모든 요청을 한 시점에 동시에 출발시킨다.
    /// </summary>
    /// <remarks>
    /// 그냥 Task.WhenAll 로 묶으면 태스크가 생성되는 순서대로 순차적으로 출발해
    /// 실제 경합이 거의 일어나지 않는다. 게이트를 두고 전원을 대기시킨 뒤 한 번에 풀어야
    /// "이벤트 오픈 순간" 에 가까운 경합이 만들어진다.
    /// </remarks>
    public async Task<IReadOnlyList<IssueCall>> IssueConcurrentlyAsync(
        long eventId, IReadOnlyList<(string UserId, Guid? RequestId)> requests)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new CountdownEvent(requests.Count);

        var tasks = requests.Select(r => Task.Run(async () =>
        {
            ready.Signal();
            await gate.Task;
            return await IssueAsync(eventId, r.UserId, r.RequestId);
        })).ToArray();

        ready.Wait(TimeSpan.FromSeconds(60));   // 전원이 출발선에 설 때까지
        gate.SetResult();

        return await Task.WhenAll(tasks);
    }
}

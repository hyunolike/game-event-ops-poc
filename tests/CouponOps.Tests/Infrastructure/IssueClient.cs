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

public sealed class IssueClient(WebApplicationFactory<Program> app)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http = app.CreateClient();

    public async Task<IssueCall> IssueAsync(long eventId, string userId, Guid? requestId = null)
    {
        var response = await _http.PostAsJsonAsync(
            $"/api/events/{eventId}/coupons/issue", new { userId, requestId });

        var body = await response.Content.ReadFromJsonAsync<IssueApiResponse>(Json);
        return new IssueCall(response.StatusCode, body);
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

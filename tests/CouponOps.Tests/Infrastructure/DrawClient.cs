using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CouponOps.Tests.Infrastructure;

public sealed record SpinPrizeBody(
    long PrizeId, int SlotIndex, string Name, long ItemId, int Qty, bool IsJackpot, bool IsBlank);

public sealed record SpinApiResponse(
    string Result, SpinPrizeBody? Prize, bool FallbackApplied, bool PityApplied, int PityCount,
    int RemainingTickets, int RemainingDraws, string? PriorResult, string RequestId, int LatencyMs);

public sealed record SpinCall(HttpStatusCode Status, SpinApiResponse? Body)
{
    public string Result => Body?.Result ?? $"HTTP {(int)Status}";
}

public sealed class DrawClient(WebApplicationFactory<Program> app)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http = app.CreateClient();

    public async Task<SpinCall> SpinAsync(long drawEventId, string userId, Guid? requestId = null)
    {
        var response = await _http.PostAsJsonAsync(
            $"/api/draws/{drawEventId}/spin", new { userId, requestId });
        var raw = await response.Content.ReadAsStringAsync();

        // 본문이 JSON 이 아니면(처리되지 않은 예외 페이지 등) 그 사실이 그대로 드러나야 한다.
        if (!raw.StartsWith('{'))
            throw new InvalidOperationException(
                $"/api/draws/{drawEventId}/spin 이 JSON 이 아닌 응답을 돌려줬습니다 "
                + $"(status {(int)response.StatusCode}): {raw[..Math.Min(400, raw.Length)]}");

        return new SpinCall(response.StatusCode, JsonSerializer.Deserialize<SpinApiResponse>(raw, Json));
    }

    public async Task<string> OddsRawAsync(long drawEventId) =>
        await _http.GetStringAsync($"/api/draws/{drawEventId}/odds");

    /// <summary>
    /// 모든 요청을 한 시점에 동시에 출발시킨다.
    /// 그냥 Task.WhenAll 로 묶으면 생성 순서대로 출발해 실제 경합이 거의 일어나지 않는다.
    /// </summary>
    public async Task<IReadOnlyList<SpinCall>> SpinConcurrentlyAsync(
        long drawEventId, IReadOnlyList<(string UserId, Guid? RequestId)> requests)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new CountdownEvent(requests.Count);

        var tasks = requests.Select(r => Task.Run(async () =>
        {
            ready.Signal();
            await gate.Task;
            return await SpinAsync(drawEventId, r.UserId, r.RequestId);
        })).ToArray();

        ready.Wait(TimeSpan.FromSeconds(60));
        gate.SetResult();

        return await Task.WhenAll(tasks);
    }
}

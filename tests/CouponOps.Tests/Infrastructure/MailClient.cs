using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CouponOps.Tests.Infrastructure;

public sealed record MailBody(
    long MailId, long PrizeId, string PrizeName, long ItemId, int Qty, string CreatedAt);

public sealed record ClaimBody(string Result, MailBody? Mail);

public sealed record ClaimCall(HttpStatusCode Status, ClaimBody? Body)
{
    public string Result => Body?.Result ?? $"HTTP {(int)Status}";
}

public sealed class MailClient(WebApplicationFactory<Program> app)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http = app.CreateClient();

    public async Task<IReadOnlyList<MailBody>> ListAsync(long drawEventId, string userId) =>
        await _http.GetFromJsonAsync<List<MailBody>>(
            $"/api/draws/{drawEventId}/mails?userId={Uri.EscapeDataString(userId)}", Json) ?? [];

    public async Task<ClaimCall> ClaimAsync(long drawEventId, long mailId, string userId)
    {
        var response = await _http.PostAsJsonAsync(
            $"/api/draws/{drawEventId}/mails/{mailId}/claim", new { userId });
        var raw = await response.Content.ReadAsStringAsync();

        if (!raw.StartsWith('{'))
            throw new InvalidOperationException(
                $"우편 수령이 JSON 이 아닌 응답을 돌려줬습니다 (status {(int)response.StatusCode}): "
                + raw[..Math.Min(400, raw.Length)]);

        return new ClaimCall(response.StatusCode, JsonSerializer.Deserialize<ClaimBody>(raw, Json));
    }

    /// <summary>같은 우편을 동시에 여러 번 수령 시도한다(더블클릭·재시도 폭주).</summary>
    public async Task<IReadOnlyList<ClaimCall>> ClaimConcurrentlyAsync(
        long drawEventId, long mailId, string userId, int attempts)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new CountdownEvent(attempts);

        var tasks = Enumerable.Range(0, attempts).Select(_ => Task.Run(async () =>
        {
            ready.Signal();
            await gate.Task;
            return await ClaimAsync(drawEventId, mailId, userId);
        })).ToArray();

        ready.Wait(TimeSpan.FromSeconds(60));
        gate.SetResult();

        return await Task.WhenAll(tasks);
    }
}

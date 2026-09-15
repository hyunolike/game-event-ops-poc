using System.Diagnostics;
using System.Text.Json;
using CouponOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace CouponOps.Api;

/// <summary>
/// 헬스체크. 배포 전환의 게이트로 쓰이므로 정직해야 한다 —
/// 준비되지 않은 인스턴스가 "정상" 이라고 답하면 무중단 배포가 중단 배포가 된다.
/// </summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // 살아있음(liveness) — 의존성을 보지 않는다.
        // 여기서 DB 를 확인하면 DB 가 잠깐 흔들릴 때 오케스트레이터가 멀쩡한 앱을 재시작시킨다.
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }))
           .AllowAnonymous();

        // 준비됨(readiness) — Redis·MSSQL 을 각각 확인한다.
        // 배포 스크립트는 이 엔드포인트가 200 을 줄 때까지 트래픽을 넘기지 않는다.
        app.MapGet("/health/ready", ReadyAsync).AllowAnonymous();

        // 편의 별칭. 운영자가 습관적으로 /health 를 친다.
        app.MapGet("/health", ReadyAsync).AllowAnonymous();

        return app;
    }

    private sealed record Probe(string Name, bool Healthy, long DurationMs, string? Error);

    private static async Task<IResult> ReadyAsync(
        AppDbContext db, IConnectionMultiplexer redis, CancellationToken ct)
    {
        // 두 검사를 동시에 돌린다. 순차로 하면 응답 시간이 합쳐지고,
        // 배포 스크립트의 폴링 주기 안에 못 들어올 수 있다.
        var sql = CheckAsync("mssql", async token =>
        {
            // SELECT 1 로는 커넥션만 확인된다. 실제로 쓰는 테이블을 한 번 건드려
            // "연결은 되는데 마이그레이션이 안 된" 상태를 걸러낸다.
            await db.Database.ExecuteSqlRawAsync("SELECT TOP 1 1 FROM Events", token);
        }, ct);

        var cache = CheckAsync("redis", async _ =>
        {
            var latency = await redis.GetDatabase().PingAsync();
            if (latency > TimeSpan.FromSeconds(2))
                throw new TimeoutException($"Redis 응답이 느립니다 ({latency.TotalMilliseconds:F0}ms)");
        }, ct);

        var probes = await Task.WhenAll(sql, cache);
        var healthy = probes.All(p => p.Healthy);

        var payload = new
        {
            status = healthy ? "healthy" : "unhealthy",
            checks = probes.ToDictionary(
                p => p.Name,
                p => new { status = p.Healthy ? "healthy" : "unhealthy", durationMs = p.DurationMs, error = p.Error }),
        };

        // 준비되지 않았으면 503. 배포 스크립트도 로드밸런서도 이 코드로 판단한다.
        return Results.Json(payload, statusCode: healthy ? 200 : StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<Probe> CheckAsync(
        string name, Func<CancellationToken, Task> probe, CancellationToken ct)
    {
        // 의존성이 응답하지 않을 때 헬스체크가 함께 멈추면 안 된다.
        // 배포 스크립트 입장에서 "느린 200" 은 "빠른 503" 보다 나쁘다.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));

        var started = Stopwatch.GetTimestamp();
        try
        {
            await probe(timeout.Token);
            return new Probe(name, true, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, null);
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException && !ct.IsCancellationRequested
                ? "검사 시간 초과 (3초)"
                : ex.Message;

            return new Probe(name, false, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, message);
        }
    }
}

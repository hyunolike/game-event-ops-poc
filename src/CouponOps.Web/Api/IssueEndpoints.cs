using CouponOps.Application;
using CouponOps.Domain;
using StackExchange.Redis;

namespace CouponOps.Api;

public sealed record IssueCouponRequest(string UserId, Guid? RequestId);

public sealed record IssueCouponResponse(
    string Result,
    string? CouponCode,
    int Remaining,
    string? PriorResult,
    string RequestId,
    int LatencyMs);

public static class IssueEndpoints
{
    public static IEndpointRouteBuilder MapIssueEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/events/{eventId:long}/coupons/issue", IssueAsync)
           .WithName("IssueCoupon");

        return app;
    }

    private static async Task<IResult> IssueAsync(
        long eventId,
        IssueCouponRequest request,
        IssueCouponService service,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
            return Results.BadRequest(new { error = "userId 는 필수입니다." });

        try
        {
            var r = await service.IssueAsync(eventId, request.UserId, request.RequestId, ct);

            var body = new IssueCouponResponse(
                Result: r.Result.ToString(),
                CouponCode: r.CouponCode,
                Remaining: r.Remaining,
                PriorResult: r.PriorResult?.ToString(),
                RequestId: r.RequestId.ToString(),
                LatencyMs: r.LatencyMs);

            return Results.Json(body, statusCode: StatusFor(r.Result));
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException)
        {
            // ── Redis 장애 시: fail-fast (fallback 하지 않는다) ────────────────────────
            //
            // DB 로 우회해 계속 발급하는 선택지가 있지만 채택하지 않았다. 이유는 하나다.
            // 재고의 원천이 둘이 되는 순간 "정확히 N개" 를 더 이상 보증할 수 없다.
            // Redis 가 잠시 끊긴 사이 DB 경로로 발급된 수량은 Redis 카운터에 반영되지 않고,
            // 복구 후 Redis 는 자기가 아는 재고로 계속 발급한다. 결과는 초과 발급이고,
            // 이 PoC 가 증명하려는 성질이 정확히 그 지점에서 깨진다.
            //
            // 초과 발급은 보상 처리가 어렵다(이미 유저에게 코드가 나갔다).
            // 반면 짧은 시간의 발급 거부는 재시도로 회복된다. 비대칭적인 비용이므로 거부를 택한다.
            // 503 + Retry-After 로 클라이언트에 재시도 가능함을 명시한다.
            loggerFactory.CreateLogger("IssueEndpoint")
                .LogError(ex, "Redis 사용 불가 — 이벤트 {EventId} 발급 요청을 거부합니다", eventId);

            return Results.Json(
                new { error = "일시적으로 발급을 처리할 수 없습니다. 잠시 후 다시 시도해 주세요." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static int StatusFor(IssueResult result) => result switch
    {
        IssueResult.Success => StatusCodes.Status200OK,

        // 멱등 재생. 클라이언트가 원하던 상태(쿠폰 보유)는 이미 달성돼 있으므로 200 이다.
        // 본문의 result 로 재시도였음을, priorResult 로 최초 결과를 구분할 수 있다.
        IssueResult.DuplicateRequest => StatusCodes.Status200OK,

        // 요청 자체는 유효하나 현재 상태에서 거부된 것 — 재시도해도 같은 답이 나온다.
        IssueResult.SoldOut or IssueResult.OutOfPeriod
            or IssueResult.LimitExceeded or IssueResult.Suspended => StatusCodes.Status409Conflict,

        _ => StatusCodes.Status503ServiceUnavailable,
    };
}

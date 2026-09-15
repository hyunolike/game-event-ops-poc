using CouponOps.Application;

namespace CouponOps.Api;

/// <summary>
/// Redis 를 쓰지 않는 발급 경로. <b>4단계 비교 측정 전용</b>이며 운영 경로가 아니다.
/// </summary>
/// <remarks>
/// 같은 규칙·같은 이력 스키마를 쓰되 발급의 원천만 DB 로 바꾼 대조군이다.
/// 비교군을 나중에 급조하면 스키마가 갈라져 수치가 불공정해지므로 처음부터 함께 둔다.
/// </remarks>
public static class IssueDbEndpoints
{
    public static IEndpointRouteBuilder MapIssueDbEndpoints(this IEndpointRouteBuilder app)
    {
        // 이벤트 행 배타 락 — 발급 전체가 한 줄로 선다
        app.MapPost("/api/events/{eventId:long}/coupons/issue-db",
                (long eventId, IssueCouponRequest req, DbIssueCouponService svc, CancellationToken ct) =>
                    IssueAsync(eventId, req, svc, DbIssueStrategy.EventRowLock, ct))
           .WithName("IssueCouponDbLock");

        // 쿠폰 행 READPAST — 잠긴 행을 건너뛰어 서로 기다리지 않는다
        app.MapPost("/api/events/{eventId:long}/coupons/issue-db-skiplocked",
                (long eventId, IssueCouponRequest req, DbIssueCouponService svc, CancellationToken ct) =>
                    IssueAsync(eventId, req, svc, DbIssueStrategy.RowSkipLocked, ct))
           .WithName("IssueCouponDbSkipLocked");

        return app;
    }

    private static async Task<IResult> IssueAsync(
        long eventId, IssueCouponRequest request, DbIssueCouponService service,
        DbIssueStrategy strategy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
            return Results.BadRequest(new { error = "userId 는 필수입니다." });

        var r = await service.IssueAsync(eventId, request.UserId, request.RequestId, strategy, ct);

        var body = new IssueCouponResponse(
            Result: r.Result.ToString(),
            CouponCode: r.CouponCode,
            Remaining: r.Remaining,
            PriorResult: r.PriorResult?.ToString(),
            RequestId: r.RequestId.ToString(),
            LatencyMs: r.LatencyMs,
            Detail: r.Detail);

        return Results.Json(body, statusCode: IssueEndpoints.StatusFor(r.Result));
    }
}

using CouponOps.Application;

namespace CouponOps.Api;

public sealed record ClaimMailRequest(string UserId);

public sealed record MailBody(
    long MailId, long PrizeId, string PrizeName, long ItemId, int Qty, string CreatedAt);

public sealed record ClaimMailResponse(string Result, MailBody? Mail);

/// <summary>
/// 보상 우편함. 게임 클라이언트가 호출한다.
/// </summary>
/// <remarks>
/// 추첨 응답이 곧 지급은 아니다 — 추첨은 우편을 만들고, 지급은 유저가 수령할 때 확정된다.
/// 연출 중 앱이 죽어도 결과는 이미 서버에 있으므로, 재접속 시 이 목록으로 복구된다.
/// </remarks>
public static class DrawMailEndpoints
{
    public static IEndpointRouteBuilder MapDrawMailEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/draws/{drawEventId:long}/mails", ListAsync).WithName("ListDrawMails");
        app.MapPost("/api/draws/{drawEventId:long}/mails/{mailId:long}/claim", ClaimAsync)
           .WithName("ClaimDrawMail");

        return app;
    }

    private static async Task<IResult> ListAsync(
        long drawEventId, string? userId, DrawMailService mails, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Results.BadRequest(new { error = "userId 는 필수입니다." });

        var rows = await mails.ListUnclaimedAsync(drawEventId, userId, ct);

        return Results.Ok(rows.Select(m => new MailBody(
            m.MailId, m.PrizeId, m.PrizeName, m.ItemId, m.ItemQty, m.CreatedAt.ToString("O"))).ToList());
    }

    private static async Task<IResult> ClaimAsync(
        long drawEventId, long mailId, ClaimMailRequest request,
        DrawMailService mails, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
            return Results.BadRequest(new { error = "userId 는 필수입니다." });

        var r = await mails.ClaimAsync(drawEventId, mailId, request.UserId, ct);

        var body = new ClaimMailResponse(
            r.Outcome.ToString(),
            r.Mail is null ? null : new MailBody(
                r.Mail.MailId, r.Mail.PrizeId, r.Mail.PrizeName,
                r.Mail.ItemId, r.Mail.ItemQty, r.Mail.CreatedAt.ToString("O")));

        return Results.Json(body, statusCode: StatusFor(r.Outcome));
    }

    internal static int StatusFor(MailClaimOutcome outcome) => outcome switch
    {
        MailClaimOutcome.Claimed => StatusCodes.Status200OK,

        // 클라이언트가 원하던 상태(수령됨)는 이미 달성돼 있다. 중복 클릭·재시도의 정상 응답이므로
        // 200 이고, 본문의 result 로 처음이 아니었음을 구분한다 — 추첨 API 의 멱등 재생과 같은 판단이다.
        MailClaimOutcome.AlreadyClaimed => StatusCodes.Status200OK,

        // 운영자가 회수했다. 유저가 다시 시도해도 결과가 바뀌지 않는다.
        MailClaimOutcome.Revoked => StatusCodes.Status409Conflict,

        _ => StatusCodes.Status404NotFound,
    };
}

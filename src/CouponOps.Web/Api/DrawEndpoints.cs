using CouponOps.Application;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using CouponOps.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace CouponOps.Api;

public sealed record SpinRequest(string UserId, Guid? RequestId);

public sealed record SpinPrizeBody(
    long PrizeId, int SlotIndex, string Name, long ItemId, int Qty, bool IsJackpot, bool IsBlank);

public sealed record SpinResponse(
    string Result,
    SpinPrizeBody? Prize,
    bool FallbackApplied,
    bool PityApplied,
    int PityCount,
    int RemainingTickets,
    int RemainingDraws,
    string? PriorResult,
    string RequestId,
    int LatencyMs);

public sealed record OddsRowBody(int SlotIndex, string Name, long ItemId, int Qty, string Percent, bool IsJackpot);

public sealed record OddsResponse(
    string EventCode, string EventName, string WeightVersionHash, int Version,
    IReadOnlyList<OddsRowBody> Rows, string Total, string SoldOutNotice);

public static class DrawEndpoints
{
    public static IEndpointRouteBuilder MapDrawEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/draws/{drawEventId:long}/spin", SpinAsync).WithName("SpinDraw");
        app.MapGet("/api/draws/{drawEventId:long}/odds", OddsAsync).WithName("DrawOdds");
        app.MapGet("/api/draws/{drawEventId:long}/users/{userId}", UserStateAsync).WithName("DrawUserState");

        return app;
    }

    private static async Task<IResult> SpinAsync(
        long drawEventId,
        SpinRequest request,
        SpinDrawService service,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
            return Results.BadRequest(new { error = "userId 는 필수입니다." });

        try
        {
            var r = await service.SpinAsync(drawEventId, request.UserId, request.RequestId, ct);

            var body = new SpinResponse(
                Result: r.Result.ToString(),
                Prize: r.Prize is null ? null : new SpinPrizeBody(
                    r.Prize.PrizeId, r.Prize.SlotIndex, r.Prize.Name,
                    r.Prize.ItemId, r.Prize.ItemQty, r.Prize.IsJackpot, r.Prize.IsBlank),
                FallbackApplied: r.FallbackApplied,
                PityApplied: r.PityApplied,
                PityCount: r.PityCount,
                RemainingTickets: r.RemainingTickets,
                RemainingDraws: r.RemainingDraws,
                PriorResult: r.PriorResult?.ToString(),
                RequestId: r.RequestId.ToString(),
                LatencyMs: r.LatencyMs);

            return Results.Json(body, statusCode: StatusFor(r.Result));
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException)
        {
            // Redis 장애 시 fail-fast. 쿠폰 발급과 같은 판단이다 —
            // 판정의 원천이 둘이 되는 순간 "재고를 넘긴 지급이 없다" 를 보증할 수 없고,
            // 이미 유저에게 지급된 아이템은 회수할 수 없다.
            // 짧은 시간의 거부는 재시도로 회복되지만 초과 지급은 회복되지 않는다. 비대칭이다.
            loggerFactory.CreateLogger("DrawEndpoint")
                .LogError(ex, "Redis 사용 불가 — 룰렛 {DrawEventId} 추첨 요청을 거부합니다", drawEventId);

            return Results.Json(
                new { error = "일시적으로 추첨을 처리할 수 없습니다. 잠시 후 다시 시도해 주세요." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// 확률 공시. 로그인 없이 열린다.
    /// </summary>
    /// <remarks>
    /// 어드민 미리보기와 <b>같은 함수</b>(<see cref="DrawOdds.Compute"/>)로 계산한다.
    /// 둘이 다른 계산을 하는 순간 언젠가 반드시 어긋나고, 어긋난 공시는 그 자체로 사고다.
    /// </remarks>
    private static async Task<IResult> OddsAsync(long drawEventId, AppDbContext db, CancellationToken ct)
    {
        var ev = await db.DrawEvents.AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == drawEventId, ct);
        if (ev is null) return Results.NotFound();

        var prizes = await db.DrawPrizes.AsNoTracking()
            .Where(p => p.DrawEventId == drawEventId).ToListAsync(ct);

        var version = ev.ActiveWeightVersionId is { } vid
            ? await db.DrawWeightVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == vid, ct)
            : null;

        var rows = DrawOdds.Compute(prizes);
        var byId = prizes.ToDictionary(p => p.Id);

        var notice = BuildSoldOutNotice(rows, byId);

        return Results.Ok(new OddsResponse(
            ev.Code, ev.Name,
            version?.SnapshotHash ?? "", version?.Version ?? 0,
            rows.Select(r => new OddsRowBody(
                r.SlotIndex, r.Name, r.ItemId, r.ItemQty, Percent(r.Percent), r.IsJackpot)).ToList(),
            Percent(DrawOdds.Sum(rows)),
            notice));
    }

    /// <summary>
    /// 소진 정책 문구. 대체(Fallback) 정책에서는 공시 확률이 실행 확률과 항상 같으므로
    /// "확률이 바뀐다" 가 아니라 "무엇으로 대체되는가" 를 밝히면 된다.
    /// </summary>
    private static string BuildSoldOutNotice(
        IReadOnlyList<OddsRow> rows, IReadOnlyDictionary<long, DrawPrize> byId)
    {
        var limited = rows.Where(r => r.Stock >= 0).ToList();
        if (limited.Count == 0) return "";

        var replacements = limited
            .Select(r => byId.TryGetValue(r.PrizeId, out var p) ? p.FallbackPrizeId : null)
            .Where(id => id is not null)
            .Select(id => byId.TryGetValue(id!.Value, out var f) ? f.Name : null)
            .Where(n => n is not null)
            .Distinct()
            .ToList();

        return replacements.Count == 0
            ? "한정 수량 경품은 소진 시 지급되지 않습니다."
            : $"한정 수량 경품 소진 시 해당 확률은 '{string.Join(", ", replacements)}' 지급으로 대체됩니다. "
              + "표시된 확률은 소진 여부와 무관하게 변하지 않습니다.";
    }

    /// <summary>유저의 남은 티켓·오늘 추첨 횟수·천장 진행도. 클라이언트가 룰렛 화면을 열 때 부른다.</summary>
    private static async Task<IResult> UserStateAsync(
        long drawEventId, string userId,
        IDrawStore store, DrawMetaCache metaCache, AppDbContext db,
        TimeProvider clock, CancellationToken ct)
    {
        var meta = await metaCache.GetAsync(drawEventId, ct);
        if (meta is null) return Results.NotFound();

        var now = clock.GetUtcNow().UtcDateTime;
        var day = DrawDay.For(now, meta.DailyResetAt);

        var tickets = await store.GetTicketsAsync(drawEventId, userId, ct);
        var used = await store.GetDrawsTodayAsync(drawEventId, userId, day, ct);
        var ev = await db.DrawEvents.AsNoTracking().SingleAsync(e => e.Id == drawEventId, ct);

        return Results.Ok(new
        {
            EventCode = meta.Code,
            Status = ev.StatusAt(now).ToString(),
            RemainingTickets = meta.TicketItemId is null ? -1 : tickets,
            DrawsToday = used,
            RemainingDraws = meta.DailyDrawLimit == 0 ? -1 : Math.Max(0, meta.DailyDrawLimit - used),
            ResetsInSeconds = (int)DrawDay.UntilNextReset(now, meta.DailyResetAt).TotalSeconds,
            PityThreshold = ev.PityThreshold,
        });
    }

    private static string Percent(decimal value) => value.ToString($"F{DrawOdds.Decimals}") + "%";

    internal static int StatusFor(DrawResult result) => result switch
    {
        DrawResult.Won => StatusCodes.Status200OK,

        // 멱등 재생. 클라이언트가 원하던 상태(추첨 완료)는 이미 달성돼 있으므로 200 이다.
        // 본문의 priorResult 로 최초 결과를, result 로 재시도였음을 구분할 수 있다.
        DrawResult.DuplicateRequest => StatusCodes.Status200OK,

        // 요청 자체는 유효하나 현재 상태에서 거부된 것 — 쿠폰 발급 API 와 같은 규약이다.
        DrawResult.InsufficientTicket or DrawResult.DailyLimitExceeded
            or DrawResult.OutOfPeriod or DrawResult.Suspended
            or DrawResult.AllPrizesSoldOut => StatusCodes.Status409Conflict,

        _ => StatusCodes.Status503ServiceUnavailable,
    };
}

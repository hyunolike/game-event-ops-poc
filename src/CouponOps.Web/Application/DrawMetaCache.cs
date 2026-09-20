using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CouponOps.Application;

/// <param name="Name">경품명. 이력에 비정규화해 남기므로 나중에 바뀌어도 과거 이력은 그대로다.</param>
public sealed record PrizeMeta(long PrizeId, int SlotIndex, string Name, bool IsBlank, bool IsJackpot);

public sealed record DrawMeta(
    long Id, string Code, string Name, TimeSpan DailyResetAt,
    int DailyDrawLimit, long? TicketItemId, int TicketCost,
    IReadOnlyDictionary<long, PrizeMeta> Prizes);

/// <summary>
/// 추첨 hot path 가 필요로 하는 이벤트 메타의 메모리 캐시.
/// </summary>
/// <remarks>
/// 추첨 한 건마다 DB 를 읽으면 Redis 로 원자성을 끌어온 의미가 없다.
/// 판정에 쓰이는 값은 전부 Redis 메타에 있고, 여기 캐시는 <b>응답을 사람이 읽을 수 있게</b>
/// 만드는 데만 쓴다(경품명, 리셋 시각). 그래서 캐시가 잠시 낡아도 추첨의 정확성에는 영향이 없다.
/// 운영자가 이벤트를 바꾸면 <see cref="Invalidate"/> 로 버린다.
/// </remarks>
public sealed class DrawMetaCache(IServiceScopeFactory scopes)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, DrawMeta> _cache = new();

    public async Task<DrawMeta?> GetAsync(long drawEventId, CancellationToken ct)
    {
        if (_cache.TryGetValue(drawEventId, out var cached)) return cached;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var ev = await db.DrawEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Id == drawEventId, ct);
        if (ev is null) return null;

        var prizes = await db.DrawPrizes.AsNoTracking()
            .Where(p => p.DrawEventId == drawEventId)
            .Select(p => new PrizeMeta(p.Id, p.SlotIndex, p.Name, p.IsBlank, p.IsJackpot))
            .ToListAsync(ct);

        var meta = new DrawMeta(
            ev.Id, ev.Code, ev.Name, ev.DailyResetAt,
            ev.DailyDrawLimit, ev.TicketItemId, ev.TicketCost,
            prizes.ToDictionary(p => p.PrizeId));

        _cache[drawEventId] = meta;
        return meta;
    }

    public void Invalidate(long drawEventId) => _cache.TryRemove(drawEventId, out _);
}

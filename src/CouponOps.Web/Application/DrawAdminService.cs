using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using CouponOps.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;

namespace CouponOps.Application;

/// <summary>운영자가 룰렛 이벤트에 가하는 모든 변경. 변경과 감사 로그가 한 트랜잭션에서 커밋된다.</summary>
public sealed class DrawAdminService(
    AppDbContext db,
    IDrawStore store,
    IAuditLogger audit,
    ICurrentActor actor,
    DrawMetaCache metaCache,
    TimeProvider clock)
{
    private const string TargetType = nameof(DrawEvent);

    /// <summary>
    /// 이벤트와 경품을 만들고 Redis 워밍업까지 끝낸다.
    /// </summary>
    /// <remarks>
    /// 저장이 세 번 나뉘는 것은 식별자 의존 때문이다 —
    /// 이벤트 Id 가 있어야 경품을 붙이고, 경품 Id 가 있어야 대체 경품과 천장 경품을 가리킬 수 있다.
    /// 마지막 저장까지 끝나야 가중치 스냅샷이 실제 Id 를 담는다.
    /// </remarks>
    public async Task<DrawEvent> CreateAndWarmAsync(
        string code, string name, DateTime startsAtUtc, DateTime endsAtUtc,
        int dailyDrawLimit, TimeSpan dailyResetAt, long? ticketItemId, int ticketCost,
        int pityThreshold, int? pitySlotIndex,
        IReadOnlyList<DrawPrize> prizes, CancellationToken ct)
    {
        var errors = DrawEvent.ValidateInput(
                code, name, startsAtUtc, endsAtUtc, dailyDrawLimit, dailyResetAt,
                ticketItemId, ticketCost, SoldOutPolicy.Fallback, pityThreshold)
            .Concat(DrawEvent.ValidatePrizes(prizes, SoldOutPolicy.Fallback, pityThreshold, pitySlotIndex))
            .ToList();
        if (errors.Count > 0) throw new ArgumentException(string.Join("; ", errors));

        var now = clock.GetUtcNow().UtcDateTime;
        var ev = DrawEvent.Create(code, name, startsAtUtc, endsAtUtc, dailyDrawLimit, dailyResetAt,
                                  ticketItemId, ticketCost, SoldOutPolicy.Fallback, pityThreshold, now);

        db.DrawEvents.Add(ev);
        await db.SaveChangesAsync(ct);   // 이벤트 Id 확보

        foreach (var p in prizes) p.AttachTo(ev.Id);
        db.DrawPrizes.AddRange(prizes);
        await db.SaveChangesAsync(ct);   // 경품 Id 확보

        var idBySlot = prizes.ToDictionary(p => p.SlotIndex, p => p.Id);
        foreach (var p in prizes) p.ResolveFallback(idBySlot);
        if (pitySlotIndex is { } slot) ev.SetPityPrize(idBySlot[slot], now);

        audit.Record(OperationAction.DrawEventCreated, TargetType, ev.Id.ToString(),
            before: null, after: DrawEventSnapshot.Of(ev));

        await db.SaveChangesAsync(ct);

        await ActivateWeightVersionAsync(ev, prizes, "이벤트 생성", now, ct);
        await WarmAsync(ev, prizes, ct);

        ev.MarkPoolWarmed(clock.GetUtcNow().UtcDateTime);
        audit.Record(OperationAction.DrawPoolWarmed, TargetType, ev.Id.ToString(),
            before: null, after: new { PrizeCount = prizes.Count, TotalWeight = prizes.Sum(p => p.Weight) });

        await db.SaveChangesAsync(ct);
        metaCache.Invalidate(ev.Id);
        return ev;
    }

    /// <summary>
    /// 이벤트 설정 수정. <b>가중치는 여기서 바꾸지 않는다</b> —
    /// 확률 변경은 새 버전 활성화(<see cref="ChangeWeightsAsync"/>)로만 가능하다.
    /// </summary>
    public async Task<DrawEvent> UpdateAsync(
        long drawEventId, string name, DateTime startsAtUtc, DateTime endsAtUtc,
        int dailyDrawLimit, TimeSpan dailyResetAt, long? ticketItemId, int ticketCost,
        int pityThreshold, int? pitySlotIndex, string? reason, CancellationToken ct)
    {
        var ev = await db.DrawEvents.SingleAsync(e => e.Id == drawEventId, ct);
        var prizes = await db.DrawPrizes.Where(p => p.DrawEventId == drawEventId).ToListAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var before = DrawEventSnapshot.Of(ev);

        var errors = DrawEvent.ValidatePrizes(prizes, ev.SoldOutPolicy, pityThreshold, pitySlotIndex);
        if (errors.Count > 0) throw new ArgumentException(string.Join("; ", errors));

        ev.Update(name, startsAtUtc, endsAtUtc, dailyDrawLimit, dailyResetAt,
                  ticketItemId, ticketCost, pityThreshold, now);
        ev.SetPityPrize(
            pitySlotIndex is { } slot ? prizes.Single(p => p.SlotIndex == slot).Id : null, now);

        audit.Record(OperationAction.DrawEventUpdated, TargetType, ev.Id.ToString(),
            before, DrawEventSnapshot.Of(ev), reason);

        await db.SaveChangesAsync(ct);

        // DB 커밋 이후에 Redis 를 맞춘다. 반대 순서면 DB 저장 실패 시 Redis 에만 반영된
        // 유령 상태가 남는다 — Redis 가 판정의 원천이므로 그쪽이 더 위험하다.
        await store.RefreshMetaAsync(ev, prizes.Sum(p => p.Weight), ct);
        metaCache.Invalidate(ev.Id);
        return ev;
    }

    /// <summary>
    /// 확률(가중치) 변경. UPDATE 가 아니라 <b>새 버전 활성화</b>다.
    /// </summary>
    /// <remarks>
    /// 진행 중인 이벤트의 확률을 덮어쓰면 그전에 일어난 추첨을 설명할 수 없게 된다.
    /// 이력은 자기가 쓴 버전을 가리키므로, 버전을 쌓아 두면 과거 추첨의 확률표가 그대로 남는다.
    /// <para>재고는 건드리지 않는다 — 이미 소진된 만큼이 되살아나면 초과 지급이 된다.</para>
    /// </remarks>
    public async Task ChangeWeightsAsync(
        long drawEventId, IReadOnlyDictionary<int, int> weightBySlot, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("확률 변경에는 사유가 필요합니다. 사유 없는 변경은 사후 조사에서 아무것도 설명하지 못합니다.");

        var ev = await db.DrawEvents.SingleAsync(e => e.Id == drawEventId, ct);
        var prizes = await db.DrawPrizes.Where(p => p.DrawEventId == drawEventId).ToListAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;

        var before = prizes.OrderBy(p => p.SlotIndex).ToDictionary(p => p.Name, p => p.Weight);

        foreach (var p in prizes)
            if (weightBySlot.TryGetValue(p.SlotIndex, out var w)) p.ChangeWeight(w);

        var errors = DrawEvent.ValidatePrizes(prizes, ev.SoldOutPolicy, ev.PityThreshold,
            ev.PityPrizeId is { } pid ? prizes.SingleOrDefault(p => p.Id == pid)?.SlotIndex : null);
        if (errors.Count > 0) throw new ArgumentException(string.Join("; ", errors));

        audit.Record(OperationAction.DrawWeightVersionActivated, TargetType, ev.Id.ToString(),
            before, prizes.OrderBy(p => p.SlotIndex).ToDictionary(p => p.Name, p => p.Weight), reason);

        await ActivateWeightVersionAsync(ev, prizes, reason, now, ct);
        await db.SaveChangesAsync(ct);

        // 누적 가중치만 갈아끼운다. 재고·천장 카운터·티켓 잔액은 그대로 유지된다.
        await store.RewriteWeightsAsync(ev, ToSlots(prizes), ct);
        metaCache.Invalidate(ev.Id);
    }

    public async Task SuspendAsync(long drawEventId, string reason, CancellationToken ct)
    {
        var ev = await db.DrawEvents.SingleAsync(e => e.Id == drawEventId, ct);
        var before = DrawEventSnapshot.Of(ev);

        ev.Suspend(clock.GetUtcNow().UtcDateTime, reason);
        audit.Record(OperationAction.DrawEventSuspended, TargetType, ev.Id.ToString(),
            before, DrawEventSnapshot.Of(ev), reason);

        await db.SaveChangesAsync(ct);

        // 중단은 즉시 효력이 있어야 한다. DB 커밋 직후 바로 Redis 에 반영한다.
        await store.SetSuspendedAsync(drawEventId, true, ct);
    }

    public async Task ResumeAsync(long drawEventId, string? reason, CancellationToken ct)
    {
        var ev = await db.DrawEvents.SingleAsync(e => e.Id == drawEventId, ct);
        var before = DrawEventSnapshot.Of(ev);

        ev.Resume(clock.GetUtcNow().UtcDateTime);
        audit.Record(OperationAction.DrawEventResumed, TargetType, ev.Id.ToString(),
            before, DrawEventSnapshot.Of(ev), reason);

        await db.SaveChangesAsync(ct);
        await store.SetSuspendedAsync(drawEventId, false, ct);
    }

    /// <summary>티켓 지급. 감사 대상이다 — 티켓은 곧 추첨 기회이고, 추첨 기회는 곧 재화다.</summary>
    public async Task<int> GrantTicketsAsync(long drawEventId, string userId, int amount, CancellationToken ct)
    {
        if (amount <= 0) throw new ArgumentException("지급 수량은 1 이상이어야 합니다.");

        var remaining = await store.GrantTicketsAsync(drawEventId, userId, amount, ct);

        audit.Record(OperationAction.DrawTicketsGranted, TargetType, drawEventId.ToString(),
            before: null, after: new { UserId = userId, Amount = amount, Remaining = remaining });
        await db.SaveChangesAsync(ct);

        return remaining;
    }

    private async Task ActivateWeightVersionAsync(
        DrawEvent ev, IReadOnlyList<DrawPrize> prizes, string reason, DateTime now, CancellationToken ct)
    {
        var current = await db.DrawWeightVersions
            .Where(v => v.DrawEventId == ev.Id && v.DeactivatedAt == null)
            .ToListAsync(ct);
        foreach (var v in current) v.Deactivate(now);

        var nextVersion = await db.DrawWeightVersions.CountAsync(v => v.DrawEventId == ev.Id, ct) + 1;
        var json = DrawSnapshot.Serialize(prizes);

        var version = DrawWeightVersion.Create(
            ev.Id, nextVersion, json, DrawSnapshot.Hash(json),
            prizes.Sum(p => p.Weight), now, actor.Id, reason);

        db.DrawWeightVersions.Add(version);
        await db.SaveChangesAsync(ct);   // 버전 Id 확보

        ev.ActivateWeightVersion(version.Id, now);
    }

    private Task WarmAsync(DrawEvent ev, IReadOnlyList<DrawPrize> prizes, CancellationToken ct) =>
        store.WarmAsync(ev, ToSlots(prizes), ct);

    private static List<DrawPrizeSlot> ToSlots(IReadOnlyList<DrawPrize> prizes) =>
        prizes.OrderBy(p => p.SlotIndex)
            .Select(p => new DrawPrizeSlot(
                p.Id, p.Weight, p.FallbackPrizeId, p.IsBlank, p.ItemId, p.ItemQty,
                p.IsUnlimited ? null : p.InitialStock))
            .ToList();
}

/// <summary>감사 로그의 Before/After 로 저장되는 룰렛 이벤트 스냅샷.</summary>
public sealed record DrawEventSnapshot(
    string Name, DateTime StartsAt, DateTime EndsAt,
    int DailyDrawLimit, string DailyResetAt, long? TicketItemId, int TicketCost,
    int PityThreshold, long? PityPrizeId, bool Suspended)
{
    public static DrawEventSnapshot Of(DrawEvent e) => new(
        e.Name, e.StartsAt, e.EndsAt, e.DailyDrawLimit, e.DailyResetAt.ToString(@"hh\:mm"),
        e.TicketItemId, e.TicketCost, e.PityThreshold, e.PityPrizeId, e.SuspendedAt is not null);
}

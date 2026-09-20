using System.Collections.Concurrent;
using CouponOps.Domain;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace CouponOps.Infrastructure.Redis;

public sealed class RedisDrawStore(
    IConnectionMultiplexer mux,
    DrawScript script,
    IOptions<DrawOptions> options) : IDrawStore
{
    private static readonly DateTime Epoch = DateTime.UnixEpoch;
    private readonly DrawOptions _opts = options.Value;
    private readonly ConcurrentDictionary<long, bool> _groupReady = new();

    private IDatabase Db => mux.GetDatabase();

    private static long ToMs(DateTime utc) => (long)(utc - Epoch).TotalMilliseconds;
    private static DateTime FromMs(long ms) => Epoch.AddMilliseconds(ms);

    public async Task<DrawOutcome> SpinAsync(
        long drawEventId, string userId, Guid requestId, DateTime nowUtc,
        long randomValue, string dayKey, bool logFailure, CancellationToken ct)
    {
        RedisKey[] keys =
        [
            RedisKeys.Draw.Meta(drawEventId),
            RedisKeys.Draw.Cum(drawEventId),
            RedisKeys.Draw.Stock(drawEventId),
            RedisKeys.Draw.Daily(drawEventId, dayKey),
            RedisKeys.Draw.Pity(drawEventId),
            RedisKeys.Draw.Tickets(drawEventId),
            RedisKeys.Draw.Request(drawEventId, requestId),
            RedisKeys.Draw.Stream(drawEventId),
            RedisKeys.Draw.Won(drawEventId),
        ];
        RedisValue[] values =
        [
            userId, requestId.ToString("N"), ToMs(nowUtc), randomValue,
            (int)_opts.IdempotencyTtl.TotalSeconds, logFailure ? "1" : "0",
            (int)_opts.DailyCounterTtl.TotalSeconds,
        ];

        var raw = (RedisValue[])(await script.EvaluateAsync(Db, keys, values))!;

        var priorRaw = (int)raw[10];
        return new DrawOutcome(
            Result: (DrawResult)(int)raw[0],
            PrizeId: (long)raw[1],
            ItemId: (long)raw[2],
            ItemQty: (int)raw[3],
            Roll: (int)raw[4],
            FallbackApplied: (int)raw[5] == 1,
            PityApplied: (int)raw[6] == 1,
            PityCountAfter: (int)raw[7],
            RemainingTickets: (int)raw[8],
            RemainingDraws: (int)raw[9],
            PriorResult: priorRaw < 0 ? null : (DrawResult)priorRaw,
            WeightVersionId: (long)raw[11],
            OriginalPrizeId: (long)raw[12],
            TotalWeight: (int)raw[13]);
    }

    public async Task WarmAsync(DrawEvent ev, IReadOnlyList<DrawPrizeSlot> slots, CancellationToken ct)
    {
        var db = Db;

        // 재워밍업은 이전 상태를 완전히 덮어써야 한다. 남은 재고·천장 카운터가 있으면
        // 이전 회차 기준으로 판정된다.
        await db.KeyDeleteAsync([
            RedisKeys.Draw.Cum(ev.Id), RedisKeys.Draw.Stock(ev.Id),
            RedisKeys.Draw.Pity(ev.Id), RedisKeys.Draw.Won(ev.Id),
        ]);

        var totalWeight = slots.Sum(s => s.Weight);

        await db.ListRightPushAsync(RedisKeys.Draw.Cum(ev.Id), [.. Cumulative(slots)]);

        // 유한 재고만 해시에 넣는다. 항목이 없다 = 무제한 — 스크립트가 HGET 한 번으로 구분한다.
        var finite = slots.Where(s => s.Stock is >= 0)
            .Select(s => new HashEntry(s.PrizeId, s.Stock!.Value)).ToArray();
        if (finite.Length > 0) await db.HashSetAsync(RedisKeys.Draw.Stock(ev.Id), finite);

        await db.HashSetAsync(RedisKeys.Draw.Meta(ev.Id), MetaEntries(ev, totalWeight));
        await db.SetAddAsync(RedisKeys.Draw.StreamRegistry, ev.Id);
    }

    public async Task RewriteWeightsAsync(
        DrawEvent ev, IReadOnlyList<DrawPrizeSlot> slots, CancellationToken ct)
    {
        var db = Db;

        // cum 만 갈아끼운다. stock·pity·tickets·won 은 그대로 둔다.
        await db.KeyDeleteAsync(RedisKeys.Draw.Cum(ev.Id));
        await db.ListRightPushAsync(RedisKeys.Draw.Cum(ev.Id), [.. Cumulative(slots)]);
        await db.HashSetAsync(RedisKeys.Draw.Meta(ev.Id), MetaEntries(ev, slots.Sum(s => s.Weight)));
    }

    public Task RefreshMetaAsync(DrawEvent ev, int totalWeight, CancellationToken ct) =>
        Db.HashSetAsync(RedisKeys.Draw.Meta(ev.Id), MetaEntries(ev, totalWeight));

    /// <summary>
    /// 누적 가중치 배열. 대체(Fallback) 정책이므로 이 배열은 이벤트 기간 내내 불변이고,
    /// 추첨 스크립트는 읽기만 한다 — 재분배 정책이었다면 매 추첨마다 다시 만들어야 했다.
    /// </summary>
    private static List<RedisValue> Cumulative(IReadOnlyList<DrawPrizeSlot> slots)
    {
        var cumulative = 0;
        var entries = new List<RedisValue>(slots.Count);
        foreach (var s in slots)
        {
            cumulative += s.Weight;
            entries.Add(string.Join('|',
                s.PrizeId, cumulative, s.FallbackPrizeId ?? 0,
                s.IsBlank ? 1 : 0, s.ItemId, s.ItemQty));
        }
        return entries;
    }

    private static HashEntry[] MetaEntries(DrawEvent ev, int totalWeight) =>
    [
        new("startsAtMs", ToMs(ev.StartsAt)),
        new("endsAtMs", ToMs(ev.EndsAt)),
        new("suspended", ev.SuspendedAt is null ? "0" : "1"),
        new("dailyLimit", ev.DailyDrawLimit),
        new("ticketCost", ev.TicketItemId is null ? 0 : ev.TicketCost),
        new("pityThreshold", ev.PityThreshold),
        new("pityPrizeId", ev.PityPrizeId ?? 0),
        new("weightVersionId", ev.ActiveWeightVersionId ?? 0),
        new("totalWeight", totalWeight),
    ];

    public Task SetSuspendedAsync(long drawEventId, bool suspended, CancellationToken ct) =>
        Db.HashSetAsync(RedisKeys.Draw.Meta(drawEventId), "suspended", suspended ? "1" : "0");

    public async Task<IReadOnlyDictionary<long, int>> GetStockAsync(long drawEventId, CancellationToken ct)
    {
        var entries = await Db.HashGetAllAsync(RedisKeys.Draw.Stock(drawEventId));
        return entries.ToDictionary(e => (long)e.Name, e => (int)e.Value);
    }

    public async Task<IReadOnlyDictionary<long, long>> GetWinCountsAsync(long drawEventId, CancellationToken ct)
    {
        var entries = await Db.HashGetAllAsync(RedisKeys.Draw.Won(drawEventId));
        return entries.ToDictionary(e => (long)e.Name, e => (long)e.Value);
    }

    public async Task<int> GetTicketsAsync(long drawEventId, string userId, CancellationToken ct)
    {
        var raw = await Db.HashGetAsync(RedisKeys.Draw.Tickets(drawEventId), userId);
        return raw.IsNullOrEmpty ? 0 : (int)raw;
    }

    public async Task<int> GrantTicketsAsync(long drawEventId, string userId, int amount, CancellationToken ct) =>
        (int)await Db.HashIncrementAsync(RedisKeys.Draw.Tickets(drawEventId), userId, amount);

    public async Task<int> GetDrawsTodayAsync(long drawEventId, string userId, string dayKey, CancellationToken ct)
    {
        var raw = await Db.HashGetAsync(RedisKeys.Draw.Daily(drawEventId, dayKey), userId);
        return raw.IsNullOrEmpty ? 0 : (int)raw;
    }

    public async Task<IReadOnlyList<long>> GetRegisteredEventIdsAsync(CancellationToken ct)
    {
        var members = await Db.SetMembersAsync(RedisKeys.Draw.StreamRegistry);
        return members.Select(m => (long)m).ToArray();
    }

    public async Task<IReadOnlyList<DrawnEntry>> ReadPendingAsync(
        long drawEventId, string consumer, int count, bool reclaimOwn, CancellationToken ct)
    {
        var key = RedisKeys.Draw.Stream(drawEventId);
        await EnsureGroupAsync(key, drawEventId);

        var entries = await Db.StreamReadGroupAsync(
            key, RedisKeys.ConsumerGroup, consumer,
            reclaimOwn ? StreamPosition.Beginning : StreamPosition.NewMessages, count);

        return entries.Select(Map).Where(e => e is not null).Select(e => e!).ToArray();
    }

    private async Task EnsureGroupAsync(RedisKey key, long drawEventId)
    {
        if (_groupReady.ContainsKey(drawEventId)) return;
        try
        {
            await Db.StreamCreateConsumerGroupAsync(key, RedisKeys.ConsumerGroup, StreamPosition.Beginning, true);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("BUSYGROUP", StringComparison.Ordinal))
        {
            // 다른 워커 인스턴스가 이미 만들었다. 정상 경로다.
        }
        _groupReady[drawEventId] = true;
    }

    private static DrawnEntry? Map(StreamEntry entry)
    {
        var f = entry.Values.ToDictionary(v => (string)v.Name!, v => v.Value);

        if (!f.TryGetValue("requestId", out var reqRaw) || !Guid.TryParse(reqRaw, out var requestId))
            return null;   // 알 수 없는 형식이 들어와도 적재 전체를 막지는 않는다.

        long L(string k) => f.TryGetValue(k, out var v) && !v.IsNullOrEmpty ? (long)v : 0L;
        int I(string k) => f.TryGetValue(k, out var v) && !v.IsNullOrEmpty ? (int)v : 0;

        return new DrawnEntry(
            StreamId: entry.Id!,
            RequestId: requestId,
            UserId: f["userId"]!,
            Result: (DrawResult)I("result"),
            PrizeId: L("prizeId"),
            OriginalPrizeId: L("originalPrizeId"),
            ItemId: L("itemId"),
            ItemQty: I("qty"),
            TicketsSpent: I("ticketsSpent"),
            FallbackApplied: I("fallback") == 1,
            PityApplied: I("pity") == 1,
            PityCountAfter: f.TryGetValue("pityAfter", out var pa) && !pa.IsNullOrEmpty ? (int)pa : -1,
            WeightVersionId: L("weightVersionId"),
            RandomValue: L("randomValue"),
            Roll: f.TryGetValue("roll", out var r) && !r.IsNullOrEmpty ? (int)r : -1,
            TotalWeight: I("totalWeight"),
            RequestedAtUtc: FromMs(L("requestedAtMs")));
    }

    public Task AcknowledgeAsync(long drawEventId, IReadOnlyList<string> streamIds, CancellationToken ct)
    {
        var ids = streamIds.Select(id => (RedisValue)id).ToArray();
        return Db.StreamAcknowledgeAsync(RedisKeys.Draw.Stream(drawEventId), RedisKeys.ConsumerGroup, ids);
    }
}

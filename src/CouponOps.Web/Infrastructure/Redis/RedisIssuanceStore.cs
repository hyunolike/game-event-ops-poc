using System.Collections.Concurrent;
using CouponOps.Domain;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace CouponOps.Infrastructure.Redis;

public sealed class RedisIssuanceStore(
    IConnectionMultiplexer mux,
    IssuanceScript script,
    IOptions<IssuanceOptions> options) : IIssuanceStore
{
    private static readonly DateTime Epoch = DateTime.UnixEpoch;
    private readonly IssuanceOptions _opts = options.Value;
    private readonly ConcurrentDictionary<long, bool> _groupReady = new();

    private IDatabase Db => mux.GetDatabase();

    private static long ToMs(DateTime utc) => (long)(utc - Epoch).TotalMilliseconds;
    private static DateTime FromMs(long ms) => Epoch.AddMilliseconds(ms);

    public async Task<IssueOutcome> IssueAsync(
        long eventId, string userId, Guid requestId, DateTime nowUtc, bool logFailure, CancellationToken ct)
    {
        RedisKey[] keys =
        [
            RedisKeys.Meta(eventId), RedisKeys.Pool(eventId), RedisKeys.Stock(eventId),
            RedisKeys.Seq(eventId), RedisKeys.Users(eventId), RedisKeys.Request(eventId, requestId),
            RedisKeys.Stream(eventId),
        ];
        RedisValue[] values =
        [
            userId, requestId.ToString("N"), ToMs(nowUtc),
            (int)_opts.IdempotencyTtl.TotalSeconds, logFailure ? "1" : "0",
            _opts.CodeSecret, eventId,
        ];

        var raw = (RedisValue[])(await script.EvaluateAsync(Db, keys, values))!;

        var result = (IssueResult)(int)raw[0];
        var code = raw[1].IsNullOrEmpty ? null : (string?)raw[1];
        var remaining = (int)raw[2];
        var priorRaw = (int)raw[3];

        return new IssueOutcome(result, code, remaining, priorRaw < 0 ? null : (IssueResult)priorRaw);
    }

    public async Task WarmAsync(CouponEvent ev, IReadOnlyList<string> preGeneratedCodes, CancellationToken ct)
    {
        var db = Db;

        // 재워밍업은 이전 상태를 완전히 덮어써야 한다. 남은 발급 기록이 있으면
        // 유저별 한도가 이전 이벤트 회차 기준으로 남아 오판한다.
        await db.KeyDeleteAsync([
            RedisKeys.Pool(ev.Id), RedisKeys.Stock(ev.Id),
            RedisKeys.Seq(ev.Id), RedisKeys.Users(ev.Id),
        ]);

        await db.HashSetAsync(RedisKeys.Meta(ev.Id),
        [
            new HashEntry("startsAtMs", ToMs(ev.StartsAt)),
            new HashEntry("endsAtMs", ToMs(ev.EndsAt)),
            new HashEntry("suspended", ev.SuspendedAt is null ? "0" : "1"),
            new HashEntry("perUserLimit", ev.PerUserLimit),
            new HashEntry("mode", (int)ev.IssuanceMode),
        ]);

        if (ev.IssuanceMode == IssuanceMode.PreGenerated)
        {
            // 수백만 건을 한 번에 RPUSH 하면 단일 명령이 Redis 를 오래 점유한다.
            // 청크로 나눠 다른 명령이 사이에 처리될 틈을 준다.
            const int chunk = 1000;
            for (var i = 0; i < preGeneratedCodes.Count; i += chunk)
            {
                ct.ThrowIfCancellationRequested();
                var slice = preGeneratedCodes.Skip(i).Take(chunk).Select(c => (RedisValue)c).ToArray();
                await db.ListRightPushAsync(RedisKeys.Pool(ev.Id), slice);
            }
        }
        else
        {
            await db.StringSetAsync(RedisKeys.Stock(ev.Id), ev.TotalQuantity);
        }

        await db.SetAddAsync(RedisKeys.StreamRegistry, ev.Id);
    }

    public Task RefreshMetaAsync(CouponEvent ev, CancellationToken ct) =>
        Db.HashSetAsync(RedisKeys.Meta(ev.Id),
        [
            new HashEntry("startsAtMs", ToMs(ev.StartsAt)),
            new HashEntry("endsAtMs", ToMs(ev.EndsAt)),
            new HashEntry("suspended", ev.SuspendedAt is null ? "0" : "1"),
            new HashEntry("perUserLimit", ev.PerUserLimit),
            new HashEntry("mode", (int)ev.IssuanceMode),
        ]);

    public async Task AppendStockAsync(
        CouponEvent ev, int additionalQuantity, IReadOnlyList<string> additionalCodes, CancellationToken ct)
    {
        if (ev.IssuanceMode == IssuanceMode.PreGenerated)
        {
            const int chunk = 1000;
            for (var i = 0; i < additionalCodes.Count; i += chunk)
            {
                ct.ThrowIfCancellationRequested();
                var slice = additionalCodes.Skip(i).Take(chunk).Select(c => (RedisValue)c).ToArray();
                await Db.ListRightPushAsync(RedisKeys.Pool(ev.Id), slice);
            }
        }
        else
        {
            // 카운터 방식은 증분만 더한다. SET 으로 덮으면 그 사이 발급된 수량이 되살아난다.
            await Db.StringIncrementAsync(RedisKeys.Stock(ev.Id), additionalQuantity);
        }
    }

    public Task SetSuspendedAsync(long eventId, bool suspended, CancellationToken ct) =>
        Db.HashSetAsync(RedisKeys.Meta(eventId), "suspended", suspended ? "1" : "0");

    public async Task<long> GetRemainingAsync(long eventId, IssuanceMode mode, CancellationToken ct)
    {
        if (mode == IssuanceMode.PreGenerated)
            return await Db.ListLengthAsync(RedisKeys.Pool(eventId));

        var raw = await Db.StringGetAsync(RedisKeys.Stock(eventId));
        return raw.IsNullOrEmpty ? 0 : (long)raw;
    }

    public async Task<IReadOnlyList<long>> GetRegisteredEventIdsAsync(CancellationToken ct)
    {
        var members = await Db.SetMembersAsync(RedisKeys.StreamRegistry);
        return members.Select(m => (long)m).ToArray();
    }

    public async Task<IReadOnlyList<IssuedEntry>> ReadPendingAsync(
        long eventId, string consumer, int count, bool reclaimOwn, CancellationToken ct)
    {
        var key = RedisKeys.Stream(eventId);
        await EnsureGroupAsync(key, eventId);

        var entries = await Db.StreamReadGroupAsync(
            key, RedisKeys.ConsumerGroup, consumer,
            reclaimOwn ? StreamPosition.Beginning : StreamPosition.NewMessages, count);

        return entries.Select(Map).Where(e => e is not null).Select(e => e!).ToArray();
    }

    private async Task EnsureGroupAsync(RedisKey key, long eventId)
    {
        if (_groupReady.ContainsKey(eventId)) return;
        try
        {
            // createStream: true — 아직 발급이 한 건도 없어 스트림이 없을 때도 그룹을 만든다.
            await Db.StreamCreateConsumerGroupAsync(key, RedisKeys.ConsumerGroup, StreamPosition.Beginning, true);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("BUSYGROUP", StringComparison.Ordinal))
        {
            // 다른 워커 인스턴스가 이미 만들었다. 정상 경로다.
        }
        _groupReady[eventId] = true;
    }

    private static IssuedEntry? Map(StreamEntry entry)
    {
        var f = entry.Values.ToDictionary(v => (string)v.Name!, v => v.Value);

        if (!f.TryGetValue("requestId", out var reqRaw) || !Guid.TryParse(reqRaw, out var requestId))
            return null;   // 스트림에 알 수 없는 형식이 들어오면 건너뛴다(적재를 막지 않는다).

        var code = f.TryGetValue("code", out var c) && !c.IsNullOrEmpty ? (string?)c : null;

        return new IssuedEntry(
            StreamId: entry.Id!,
            RequestId: requestId,
            UserId: f["userId"]!,
            Result: (IssueResult)(int)f["result"],
            CouponCode: code,
            RequestedAtUtc: FromMs((long)f["requestedAtMs"]));
    }

    public Task AcknowledgeAsync(long eventId, IReadOnlyList<string> streamIds, CancellationToken ct)
    {
        var ids = streamIds.Select(id => (RedisValue)id).ToArray();
        return Db.StreamAcknowledgeAsync(RedisKeys.Stream(eventId), RedisKeys.ConsumerGroup, ids);
    }
}

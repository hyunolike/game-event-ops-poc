using System.Reflection;
using StackExchange.Redis;

namespace CouponOps.Infrastructure.Redis;

/// <summary>
/// 내장된 Lua 스크립트를 EVALSHA 로 실행한다.
/// </summary>
/// <remarks>
/// 스크립트 본문을 매 호출마다 보내면 요청 1건당 수 KB 가 네트워크로 나간다.
/// 초당 수천 건 규모에서는 무시할 수 없으므로 SHA 로만 호출하고,
/// 서버가 스크립트 캐시를 잃은 경우(재시작, SCRIPT FLUSH)에만 재적재 후 한 번 재시도한다.
/// </remarks>
public abstract class LuaScript
{
    private readonly IConnectionMultiplexer _mux;
    private readonly string _source;
    private byte[]? _sha;

    protected LuaScript(IConnectionMultiplexer mux, string fileName)
    {
        _mux = mux;
        _source = ReadEmbedded(fileName);
    }

    private static string ReadEmbedded(string fileName)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith(fileName, StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>모든 노드에 스크립트를 적재하고 SHA 를 캐시한다.</summary>
    public async Task<byte[]> EnsureLoadedAsync()
    {
        byte[]? sha = null;
        foreach (var endpoint in _mux.GetEndPoints())
        {
            var server = _mux.GetServer(endpoint);
            if (server.IsReplica || !server.IsConnected) continue;
            sha = await server.ScriptLoadAsync(_source);
        }

        if (sha is null) throw new RedisConnectionException(
            ConnectionFailureType.UnableToResolvePhysicalConnection,
            "스크립트를 적재할 수 있는 Redis 마스터 노드가 없습니다.");

        return _sha = sha;
    }

    public async Task<RedisResult> EvaluateAsync(IDatabase db, RedisKey[] keys, RedisValue[] values)
    {
        var sha = _sha ?? await EnsureLoadedAsync();
        try
        {
            return await db.ScriptEvaluateAsync(sha, keys, values);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("NOSCRIPT", StringComparison.Ordinal))
        {
            // 서버가 스크립트 캐시를 잃었다. 재적재 후 한 번만 재시도한다.
            sha = await EnsureLoadedAsync();
            return await db.ScriptEvaluateAsync(sha, keys, values);
        }
    }
}

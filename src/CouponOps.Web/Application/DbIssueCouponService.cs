using System.Data;
using System.Diagnostics;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CouponOps.Application;

/// <summary>DB 만으로 발급하는 두 가지 전략. 4단계 비교 측정의 대조군이다.</summary>
public enum DbIssueStrategy
{
    /// <summary>
    /// 이벤트 행에 배타 락을 걸어 발급 전체를 직렬화한다.
    /// Redis 없이 "정확히 N개" 를 보증할 때 가장 먼저 떠오르는 구현이다.
    /// </summary>
    EventRowLock,

    /// <summary>
    /// 이벤트 행을 잠그지 않고, 미발급 쿠폰 행 하나를 READPAST 로 집어 온다.
    /// 잠긴 행은 건너뛰므로 발급이 서로를 기다리지 않는다. DB 만으로 할 수 있는 최선에 가깝다.
    /// </summary>
    RowSkipLocked,
}

/// <summary>
/// Redis 를 쓰지 않고 DB 트랜잭션만으로 발급한다.
/// </summary>
/// <remarks>
/// <b>이 서비스는 비교 측정용이다.</b> 실제 발급 경로는 <see cref="IssueCouponService"/> 다.
/// 공정한 비교를 위해 Redis 경로와 <b>같은 규칙</b>(기간·중단·유저 한도·멱등)을 강제하고,
/// 같은 <c>IssuanceLogs</c> 스키마에 기록한다. 다른 점은 단 하나 —
/// 여기서는 이력 적재가 <b>동기</b>이고, 발급의 원천이 DB 라는 것이다.
///
/// EF Core 대신 원시 SQL 을 쓰는 이유는 잠금 힌트(UPDLOCK / HOLDLOCK / READPAST)를
/// 정확히 제어해야 하기 때문이다. 이 실험의 주제가 바로 그 잠금 동작이다.
/// </remarks>
public sealed class DbIssueCouponService(AppDbContext db, TimeProvider clock)
{
    public async Task<IssueCouponResult> IssueAsync(
        long eventId, string userId, Guid? requestId, DbIssueStrategy strategy, CancellationToken ct)
    {
        var rid = requestId ?? Guid.NewGuid();
        var path = strategy == DbIssueStrategy.EventRowLock ? "db-lock" : "db-readpast";
        var started = Stopwatch.GetTimestamp();
        var now = clock.GetUtcNow().UtcDateTime;

        await using var conn = new SqlConnection(db.Database.GetConnectionString());

        try
        {
            await conn.OpenAsync(ct);
        }
        catch (InvalidOperationException)
        {
            // 커넥션 풀 고갈. 이 방식의 구조적 특성이다 — 요청 하나가 트랜잭션이 끝날 때까지
            // 커넥션을 붙들고 있으므로, 잠금 대기가 길어지면 풀이 먼저 바닥난다.
            // (Redis 경로는 발급 중 DB 커넥션을 전혀 잡지 않는다.)
            return SystemFailure(rid, started, "connection-pool-exhausted");
        }

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        try
        {
            var outcome = await IssueCoreAsync(conn, tx, eventId, userId, rid, strategy, path, now, ct);
            await tx.CommitAsync(ct);

            var latency = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return outcome with { LatencyMs = latency };
        }
        catch (SqlException ex) when (ex.Number is 1205 or -2)
        {
            // 1205 = 교착 상태 희생자, -2 = 명령 타임아웃.
            // 둘 다 "DB 가 경합을 못 버텼다" 는 신호다. 부하 테스트에서 이 비율이 핵심 지표가 된다.
            await SafeRollbackAsync(tx, ct);
            return SystemFailure(rid, started, ex.Number == 1205 ? "deadlock" : "command-timeout");
        }
        catch
        {
            await SafeRollbackAsync(tx, ct);
            throw;
        }
    }

    private static IssueCouponResult SystemFailure(Guid rid, long started, string detail) =>
        new(IssueResult.SystemError, null, -1, null, rid,
            (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds, detail);

    private static async Task SafeRollbackAsync(SqlTransaction tx, CancellationToken ct)
    {
        try { await tx.RollbackAsync(ct); } catch { /* 이미 정리된 트랜잭션 */ }
    }

    private async Task<IssueCouponResult> IssueCoreAsync(
        SqlConnection conn, SqlTransaction tx, long eventId, string userId, Guid rid,
        DbIssueStrategy strategy, string path, DateTime now, CancellationToken ct)
    {
        // ── 1. 멱등 검사 ────────────────────────────────────────────────────
        // Redis 경로와 같은 순서다. UPDLOCK+HOLDLOCK 으로 RequestId 키 범위를 잡아,
        // 같은 요청이 동시에 두 번 들어와도 한쪽만 진행하게 한다.
        await using (var cmd = Cmd(conn, tx, """
            SELECT Result, CouponCode FROM IssuanceLogs WITH (UPDLOCK, HOLDLOCK)
            WHERE RequestId = @rid;
            """))
        {
            cmd.Parameters.AddWithValue("@rid", rid);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var priorResult = (IssueResult)reader.GetByte(0);
                var priorCode = reader.IsDBNull(1) ? null : reader.GetString(1);
                return new IssueCouponResult(
                    IssueResult.DuplicateRequest, priorCode, -1, priorResult, rid, 0);
            }
        }

        // ── 2. 이벤트 로드 ──────────────────────────────────────────────────
        // EventRowLock 전략은 여기서 이벤트 행을 배타 잠금한다. 이 순간부터 같은 이벤트의
        // 모든 발급이 한 줄로 선다 — 정확성은 얻지만 처리량이 이 한 행에 묶인다.
        var eventLockHint = strategy == DbIssueStrategy.EventRowLock ? "WITH (UPDLOCK, ROWLOCK)" : "";

        DateTime startsAt, endsAt;
        bool suspended;
        int perUserLimit;

        await using (var cmd = Cmd(conn, tx, $"""
            SELECT StartsAt, EndsAt, SuspendedAt, PerUserLimit
            FROM Events {eventLockHint}
            WHERE Id = @e;
            """))
        {
            cmd.Parameters.AddWithValue("@e", eventId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return Fail(IssueResult.SystemError, rid);

            startsAt = reader.GetDateTime(0);
            endsAt = reader.GetDateTime(1);
            suspended = !reader.IsDBNull(2);
            perUserLimit = reader.GetInt32(3);
        }

        // ── 3. 거부 검증 ────────────────────────────────────────────────────
        if (suspended) return await LogFailAsync(conn, tx, eventId, userId, rid, IssueResult.Suspended, path, now, ct);
        if (now < startsAt || now >= endsAt)
            return await LogFailAsync(conn, tx, eventId, userId, rid, IssueResult.OutOfPeriod, path, now, ct);

        // 유저별 한도. UPDLOCK+HOLDLOCK 이 (UserId, RequestedAt) 인덱스에 키 범위 잠금을 걸어
        // 같은 유저의 동시 삽입을 막는다. 전역이 아니라 유저 단위로만 직렬화된다.
        await using (var cmd = Cmd(conn, tx, """
            SELECT COUNT(*) FROM IssuanceLogs WITH (UPDLOCK, HOLDLOCK)
            WHERE EventId = @e AND UserId = @u AND Result = 1;
            """))
        {
            cmd.Parameters.AddWithValue("@e", eventId);
            cmd.Parameters.AddWithValue("@u", userId);
            var issued = (int)(await cmd.ExecuteScalarAsync(ct))!;
            if (issued >= perUserLimit)
                return await LogFailAsync(conn, tx, eventId, userId, rid, IssueResult.LimitExceeded, path, now, ct);
        }

        // ── 4. 쿠폰 확보 ────────────────────────────────────────────────────
        // 단일 UPDATE 로 "고르고 잠그고 바꾸기" 를 한 번에 한다. SELECT 후 UPDATE 로 나누면
        // 두 명령 사이에 다른 트랜잭션이 같은 행을 집어갈 수 있다.
        //
        // RowSkipLocked 는 여기에 READPAST 를 더한다 — 다른 트랜잭션이 잠근 행은 기다리지 않고
        // 건너뛰고 다음 쿠폰을 집는다. 쿠폰은 서로 구분되지 않으므로 어느 것을 줘도 같다.
        var couponHint = strategy == DbIssueStrategy.RowSkipLocked
            ? "WITH (UPDLOCK, READPAST, ROWLOCK)"
            : "WITH (UPDLOCK, ROWLOCK)";

        long couponId;
        string couponCode;

        await using (var cmd = Cmd(conn, tx, $"""
            UPDATE TOP (1) c
            SET Status = 1, IssuedToUserId = @u, IssuedAt = @now
            OUTPUT inserted.Id, inserted.Code
            FROM Coupons AS c {couponHint}
            WHERE c.EventId = @e AND c.Status = 0;
            """))
        {
            cmd.Parameters.AddWithValue("@e", eventId);
            cmd.Parameters.AddWithValue("@u", userId);
            cmd.Parameters.AddWithValue("@now", now);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                await reader.CloseAsync();
                return await LogFailAsync(conn, tx, eventId, userId, rid, IssueResult.SoldOut, path, now, ct);
            }

            couponId = reader.GetInt64(0);
            couponCode = reader.GetString(1);
        }

        // ── 5. 이력 적재 (동기) ─────────────────────────────────────────────
        // Redis 경로는 여기서 스트림에 넣고 끝내지만, DB 경로는 같은 트랜잭션 안에서 기록한다.
        // 그것이 이 방식의 정의이자 비용이다.
        await using (var cmd = Cmd(conn, tx, """
            INSERT INTO IssuanceLogs
                (RequestId, EventId, UserId, Result, CouponId, CouponCode,
                 RequestedAt, PersistedAt, PersistenceLagMs, IssuePath)
            VALUES (@rid, @e, @u, 1, @cid, @code, @now, @now, 0, @path);
            """))
        {
            cmd.Parameters.AddWithValue("@rid", rid);
            cmd.Parameters.AddWithValue("@e", eventId);
            cmd.Parameters.AddWithValue("@u", userId);
            cmd.Parameters.AddWithValue("@cid", couponId);
            cmd.Parameters.AddWithValue("@code", couponCode);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@path", path);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        return new IssueCouponResult(IssueResult.Success, couponCode, -1, null, rid, 0);
    }

    private static async Task<IssueCouponResult> LogFailAsync(
        SqlConnection conn, SqlTransaction tx, long eventId, string userId, Guid rid,
        IssueResult result, string path, DateTime now, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, """
            INSERT INTO IssuanceLogs
                (RequestId, EventId, UserId, Result, CouponCode,
                 RequestedAt, PersistedAt, PersistenceLagMs, IssuePath)
            VALUES (@rid, @e, @u, @r, NULL, @now, @now, 0, @path);
            """);

        cmd.Parameters.AddWithValue("@rid", rid);
        cmd.Parameters.AddWithValue("@e", eventId);
        cmd.Parameters.AddWithValue("@u", userId);
        cmd.Parameters.AddWithValue("@r", (byte)result);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@path", path);
        await cmd.ExecuteNonQueryAsync(ct);

        return Fail(result, rid);
    }

    private static IssueCouponResult Fail(IssueResult result, Guid rid) =>
        new(result, null, -1, null, rid, 0);

    private static SqlCommand Cmd(SqlConnection conn, SqlTransaction tx, string sql) =>
        new(sql, conn, tx) { CommandTimeout = 30 };
}

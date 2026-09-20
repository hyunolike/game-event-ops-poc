using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CouponOps.Application;

public enum MailClaimOutcome
{
    Claimed = 0,
    /// <summary>이미 수령한 우편. 재시도·중복 클릭의 정상 응답이다.</summary>
    AlreadyClaimed = 1,
    /// <summary>운영자가 회수한 우편.</summary>
    Revoked = 2,
    /// <summary>존재하지 않거나 이 유저의 것이 아니다. 둘을 구분해 알리지 않는다.</summary>
    NotFound = 3,
}

public sealed record MailView(
    long MailId, long PrizeId, string PrizeName, long ItemId, int ItemQty, DateTime CreatedAt);

public sealed record MailClaimResult(MailClaimOutcome Outcome, MailView? Mail);

/// <summary>
/// 보상 우편의 수령과 회수.
/// </summary>
/// <remarks>
/// 추첨 결과를 인벤토리에 직접 꽂지 않고 우편함을 거치는 이유는 설계 문서 8장에 있다.
/// 그중 이 클래스가 실제로 떠받치는 것은 <b>회수 가능성</b>이다 —
/// 잘못 설정된 경품을 <b>미수령분만</b> 되돌릴 수 있는 것은 우편함이 있기 때문이고,
/// 인벤토리에 들어간 뒤에는 불가능하다.
/// </remarks>
public sealed class DrawMailService(
    AppDbContext db, IAuditLogger audit, TimeProvider clock)
{
    /// <summary>한 번의 회수에서 처리하는 배치 크기. 트랜잭션을 너무 오래 잡지 않기 위함이다.</summary>
    private const int RevokeBatchSize = 500;

    public async Task<IReadOnlyList<MailView>> ListUnclaimedAsync(
        long drawEventId, string userId, CancellationToken ct) =>
        await db.DrawRewardMails.AsNoTracking()
            .Where(m => m.DrawEventId == drawEventId && m.UserId == userId
                        && m.ClaimedAt == null && m.RevokedAt == null)
            .OrderBy(m => m.Id)
            .Select(m => new MailView(m.Id, m.PrizeId, m.PrizeName, m.ItemId, m.ItemQty, m.CreatedAt))
            .ToListAsync(ct);

    /// <summary>
    /// 우편 한 통을 수령한다.
    /// </summary>
    /// <remarks>
    /// 동시에 여러 번 눌러도 정확히 한 번만 성공한다. 규칙은 <see cref="DrawRewardMail.TryClaim"/>
    /// 한 곳에 있고, 읽은 뒤 다른 요청이 먼저 수령한 경우는 RowVersion 이 잡는다.
    /// <para>
    /// 다른 유저의 우편을 요청하면 <see cref="MailClaimOutcome.NotFound"/> 다 —
    /// "남의 것" 과 "없는 것" 을 구분해 알려주면 우편 존재 여부를 열거할 수 있다.
    /// </para>
    /// </remarks>
    public async Task<MailClaimResult> ClaimAsync(
        long drawEventId, long mailId, string userId, CancellationToken ct)
    {
        var mail = await db.DrawRewardMails.SingleOrDefaultAsync(
            m => m.Id == mailId && m.DrawEventId == drawEventId && m.UserId == userId, ct);

        if (mail is null) return new MailClaimResult(MailClaimOutcome.NotFound, null);

        var view = new MailView(mail.Id, mail.PrizeId, mail.PrizeName, mail.ItemId, mail.ItemQty, mail.CreatedAt);

        if (mail.RevokedAt is not null) return new MailClaimResult(MailClaimOutcome.Revoked, view);
        if (!mail.TryClaim(clock.GetUtcNow().UtcDateTime))
            return new MailClaimResult(MailClaimOutcome.AlreadyClaimed, view);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // 읽은 뒤 다른 요청이 먼저 수령했다. 이것은 오류가 아니라 정상 경합의 결과다.
            // EF 가 추적 중인 엔티티가 더럽혀진 채 남지 않도록 떼어 낸다.
            db.Entry(mail).State = EntityState.Detached;
            return new MailClaimResult(MailClaimOutcome.AlreadyClaimed, view);
        }

        return new MailClaimResult(MailClaimOutcome.Claimed, view);
    }

    /// <summary>
    /// 미수령 우편을 회수한다. 이미 수령한 우편은 건드리지 않는다 — 아이템이 이미 유저 손에 있다.
    /// </summary>
    /// <param name="prizeId">특정 경품만 회수할 때 지정한다. null 이면 이벤트 전체.</param>
    /// <returns>회수된 우편 수.</returns>
    public async Task<int> RevokeUnclaimedAsync(
        long drawEventId, long? prizeId, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("회수 사유는 필수입니다. 사유 없는 회수는 사후 조사에서 아무것도 설명하지 못합니다.");

        var now = clock.GetUtcNow().UtcDateTime;
        var revoked = 0;

        // 배치로 나눠 처리하되 전체를 한 트랜잭션에 둔다 —
        // 중간에 실패해 "절반만 회수된" 상태가 남으면 무엇을 되돌려야 하는지 알 수 없다.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        while (true)
        {
            var batch = await db.DrawRewardMails
                .Where(m => m.DrawEventId == drawEventId
                            && m.ClaimedAt == null && m.RevokedAt == null
                            && (prizeId == null || m.PrizeId == prizeId))
                .OrderBy(m => m.Id)
                .Take(RevokeBatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0) break;

            foreach (var mail in batch)
                if (mail.TryRevoke(now, reason)) revoked++;

            await db.SaveChangesAsync(ct);
        }

        audit.Record(OperationAction.DrawRewardsRevoked, nameof(DrawEvent), drawEventId.ToString(),
            before: null, after: new { PrizeId = prizeId, RevokedCount = revoked }, reason);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return revoked;
    }
}

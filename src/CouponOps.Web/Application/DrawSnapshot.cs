using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using CouponOps.Domain;

namespace CouponOps.Application;

/// <summary>가중치 버전에 박제되는 슬롯 하나. 이 구조만으로 추첨을 재계산할 수 있어야 한다.</summary>
public sealed record PrizeSnapshot(
    long PrizeId, int SlotIndex, string Name, long ItemId, int ItemQty,
    int Weight, int Stock, bool IsBlank, bool IsJackpot, long? FallbackPrizeId);

/// <summary>
/// 확률표 스냅샷의 직렬화·해시·재현 검증.
/// </summary>
/// <remarks>
/// 이 클래스의 존재 이유는 <b>사후 검증</b>이다.
/// <see cref="Recompute"/> 가 성립하지 않는 설계는 "그때 왜 이게 나왔는가" 에 답할 수 없는 설계다.
/// </remarks>
public static class DrawSnapshot
{
    private static readonly JsonSerializerOptions Json = new()
    {
        // 감사 대상 문서다. 한글 경품명이 \uXXXX 로 저장되면 사람이 읽을 수 없다.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>슬롯 순서를 고정해 직렬화한다 — 순서가 흔들리면 같은 표의 해시가 달라진다.</summary>
    public static string Serialize(IEnumerable<DrawPrize> prizes) =>
        JsonSerializer.Serialize(
            prizes.OrderBy(p => p.SlotIndex).Select(p => new PrizeSnapshot(
                p.Id, p.SlotIndex, p.Name, p.ItemId, p.ItemQty,
                p.Weight, p.InitialStock, p.IsBlank, p.IsJackpot, p.FallbackPrizeId)).ToList(),
            Json);

    public static IReadOnlyList<PrizeSnapshot> Deserialize(string snapshotJson) =>
        JsonSerializer.Deserialize<List<PrizeSnapshot>>(snapshotJson, Json)
        ?? throw new InvalidOperationException("가중치 스냅샷을 읽을 수 없습니다.");

    public static string Hash(string snapshotJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshotJson))).ToLowerInvariant();

    public static int TotalWeight(IEnumerable<PrizeSnapshot> snapshot) => snapshot.Sum(p => p.Weight);

    /// <summary>
    /// 저장된 이력으로 추첨을 재계산한다.
    /// </summary>
    /// <remarks>
    /// 난수를 앱에서 뽑아 이력에 남긴 덕분에 가능하다(설계 문서 결정 3).
    /// 돌려주는 것은 <b>대체 치환 전</b>의 슬롯이다 — 치환은 당시 재고에 의존하므로
    /// 순수 함수로 재현되지 않는다. 그래서 이력에 <see cref="DrawLog.OriginalPrizeId"/> 를 따로 남긴다.
    /// </remarks>
    public static long Recompute(
        IReadOnlyList<PrizeSnapshot> snapshot, long randomValue, bool pityApplied, long? pityPrizeId)
    {
        if (pityApplied)
            return pityPrizeId ?? throw new InvalidOperationException(
                "천장이 적용된 이력인데 천장 경품이 지정돼 있지 않습니다.");

        var total = TotalWeight(snapshot);
        if (total <= 0) throw new InvalidOperationException("스냅샷의 가중치 합이 0입니다.");

        var roll = (int)(randomValue % total);

        var cumulative = 0;
        foreach (var p in snapshot.OrderBy(p => p.SlotIndex))
        {
            cumulative += p.Weight;
            if (roll < cumulative) return p.PrizeId;
        }

        throw new InvalidOperationException("누적 가중치가 합에 도달하지 못했습니다 — 스냅샷이 손상됐습니다.");
    }
}

/// <param name="Matches">저장된 결과와 재계산 결과가 같은가. false 면 즉시 조사 대상이다.</param>
public sealed record DrawVerification(
    long LogId, long StoredPrizeId, long RecomputedPrizeId, bool Matches,
    int Version, string SnapshotHash, long RandomValue, int Roll, bool PityApplied, bool FallbackApplied);

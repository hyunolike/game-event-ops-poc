# 4단계 — 룰렛(확률 지급) 이벤트 설계안

> 돌림판을 돌려 확률로 게임 아이템을 지급하는 이벤트.
> 기존 선착순 쿠폰 엔진의 뼈대(멱등·원자성·감사 로그)를 그대로 쓰되,
> **결과가 확정적이지 않다**는 한 가지 차이가 만드는 모든 문제를 다룬다.

---

## 0. 쿠폰과 무엇이 같고 무엇이 다른가

설계를 시작하기 전에 재사용 가능한 부분을 먼저 가른다.

### 그대로 쓰는 것

| 자산 | 재사용 방식 |
|---|---|
| 상태 파생 (`StatusAt` / `IsStatus`) | 동일. 저장하는 건 `SuspendedAt` 뿐 |
| 멱등 키 (`RequestId` UNIQUE) | 동일. 재시도는 **같은 경품**을 돌려받는다 |
| Redis 원자 처리 + 아웃박스 스트림 | 동일. 스크립트 내용만 다르다 |
| 비동기 DB 적재 워커 | 동일. 스트림 하나가 늘어날 뿐 |
| 감사 로그 (전/후 값, 수행자, IP, 사유) | 동일. `OperationAction` 값만 추가 |
| RBAC, 강제 중단 3중 확인 | 동일 |

### 새로 풀어야 하는 것

| 문제 | 쿠폰 | 룰렛 |
|---|---|---|
| 재고 | 단일 카운터 1개 | **경품별 재고 N개** (다차원) |
| 결과 | 성공 아니면 실패 | **성공인데 무엇을 받았는지가 다르다** |
| 실패의 의미 | 소진 = 못 받음 | 꽝 = 받긴 받았다(꽝도 결과) |
| 입장 비용 | 없음 | **티켓/재화 차감** — 차감과 지급이 한 단위여야 함 |
| 사후 검증 | 코드가 있냐 없냐 | **"그때 확률이 뭐였고 왜 이게 나왔나"를 재현**해야 함 |
| 법적 요건 | 없음 | **확률 정보 표시 의무** (게임산업법, 2024.3 시행) |

마지막 두 줄이 이 문서의 절반을 차지한다. 확률 지급은 **기술 문제이기 전에 감사(audit) 문제**다.

---

## 1. 핵심 설계 결정 3가지

### 결정 1 — 가중치는 정수로 저장한다 (확률 소수 저장 금지)

```
❌  prize.probability = 0.005      (double)
✅  prize.weight      = 5          (int),  총합 1000 → 0.5%
```

이유 셋.

1. **부동소수 합산 오차**가 없다. `0.1 + 0.2 != 0.3` 인 세계에서 "확률 합이 정확히 1.0" 을 검증할 수 없다.
2. 슬롯을 하나 추가할 때 **나머지를 건드리지 않아도 된다.** 확률로 저장하면 하나 추가할 때마다 전체를 재계산해야 하고, 그 과정에서 반올림 오차가 쌓인다.
3. 추첨이 **정수 나머지 연산**으로 끝난다 — `roll = rand % totalWeight`. Lua 안에서 부동소수를 쓰지 않는다.

공시 확률은 `weight / totalWeight` 로 **표시할 때 계산**한다. 저장하지 않는다 — 상태 파생 원칙과 같다.

### 결정 2 — 재고가 소진된 경품을 어떻게 다룰 것인가 ★ 최대 함정

세 가지 정책이 있고, 무엇을 고르든 **공시 확률의 의미가 달라진다.**

| 정책 | 동작 | 문제 |
|---|---|---|
| **(a) 재분배** Renormalize | 소진 슬롯을 빼고 남은 가중치로 다시 뽑음 | **공시 확률과 실제 확률이 달라진다.** 1등이 소진되면 2등 확률이 저절로 올라감. 공시 위반 리스크 |
| **(b) 대체** Fallback ★권장 | 소진 슬롯이 뽑히면 지정된 대체 경품(보통 "꽝"/소모품)으로 치환 | 유저 체감이 나쁨. 단 **공시 확률 = 실행 확률**이 유지됨 |
| **(c) 무한 재고** | 일반 소모품은 재고를 두지 않음(`stock = -1`) | 한정 경품에는 못 씀 |

**권장은 (b) + (c) 혼합이다.**

```
잭팟/한정 경품  → stock 유한 + fallbackPrizeId 지정
일반 소모품     → stock = -1 (무한)
꽝              → stock = -1, fallback 대상
```

(a)를 고르면 **"공시된 확률"이 거짓이 되는 순간이 존재**한다. 그 순간을 유저가 알 방법이 없다는 점이 문제다.
어느 쪽을 고르든 **정책을 어드민에서 선택하게 하고, 선택에 따라 공시 문구가 자동으로 바뀌어야 한다.** 이건 코드가 아니라 제품 요구사항이다.

> 구현상으로도 (b)가 단순하다. (a)는 소진 상태에 따라 누적 가중치 배열을 매 추첨마다 다시 만들어야 하므로
> Lua 안에서 O(N) 재계산이 들어가고, "재계산 중 다른 슬롯이 소진" 같은 경합이 생긴다.
> (b)는 **누적 가중치가 이벤트 기간 내내 불변**이라 워밍업 때 한 번 만들고 끝이다.

### 결정 3 — 난수는 Lua 안에서 뽑지 않는다 ★

Redis Lua의 `math.random` 을 쓰지 않는다. 대신 **앱(.NET)에서 `RandomNumberGenerator` 로 뽑아 `ARGV` 로 넘긴다.**

```csharp
Span<byte> buf = stackalloc byte[8];
RandomNumberGenerator.Fill(buf);
var randomValue = BinaryPrimitives.ReadUInt64LittleEndian(buf) >> 1;  // 부호 비트 제거
```

이유 셋.

1. **원자성이 필요한 건 "선택 + 차감"이지 "난수 생성"이 아니다.** 난수는 순수 함수의 입력일 뿐이고, 밖에서 만들어 넣어도 원자성이 조금도 약해지지 않는다.
2. **재현 가능성.** 난수를 로그에 그대로 남길 수 있다. 사후에 `(randomValue, weightVersion)` 만으로 추첨을 그대로 재계산해 검증할 수 있다 — Lua 내부에서 뽑으면 이게 불가능하다.
3. Redis Lua의 난수는 **스크립트 실행마다 시드가 리셋**되는 구현 특성이 있어 결정성/복제 문제를 피하려는 제약을 받는다. 애초에 암호학적 품질도 아니다.

Lua는 넘겨받은 난수로 **"어느 슬롯인지 결정하고 재고를 차감"** 하는 일만 원자적으로 한다.

---

## 2. 도메인 모델

```
DrawEvent 1 ── * DrawPrize            경품(슬롯)
DrawEvent 1 ── * DrawWeightVersion    가중치 스냅샷 (append-only)
DrawEvent 1 ── * DrawLog              추첨 이력 (성공/실패 전부)
DrawEvent 1 ── * DrawUserState        유저별 상태 (천장·일일횟수·티켓)
```

### DrawEvent

`CouponEvent` 와 거의 같다. 기간·중단·파생 상태는 동일하게 가져간다.

```csharp
public class DrawEvent
{
    public long Id { get; private set; }
    public string Code { get; private set; }          // 외부 노출 식별자, 불변
    public string Name { get; private set; }

    public DateTime StartsAt { get; private set; }    // UTC
    public DateTime EndsAt { get; private set; }      // UTC

    /// <summary>유저당 1일 추첨 횟수 제한. 0 = 무제한.</summary>
    public int DailyDrawLimit { get; private set; }

    /// <summary>일일 카운터 리셋 기준 시각(로컬 기준 시, 예: 04:00). UTC 오프셋과 함께 저장.</summary>
    public TimeSpan DailyResetAt { get; private set; }
    public string ResetTimeZoneId { get; private set; }   // "Asia/Seoul"

    /// <summary>입장 비용. null = 무료(일일 횟수로만 제한).</summary>
    public long? TicketItemId { get; private set; }
    public int TicketCost { get; private set; }

    /// <summary>재고 소진 정책. 결정 2 참조.</summary>
    public SoldOutPolicy SoldOutPolicy { get; private set; }

    /// <summary>
    /// 천장. 0 = 사용 안 함. <b>N회 안에 반드시 당첨</b> — N번째 추첨이 확정이다.
    /// (N회 꽝 뒤 N+1번째로 구현하면 "10회 천장" 이 11번째가 되어 유저가 속았다고 느낀다.)
    /// </summary>
    public int PityThreshold { get; private set; }
    public long? PityPrizeId { get; private set; }

    /// <summary>현재 활성 가중치 버전. 추첨은 항상 이 버전을 기준으로 한다.</summary>
    public long ActiveWeightVersionId { get; private set; }

    public DateTime? SuspendedAt { get; private set; }
    public string? SuspendReason { get; private set; }
    public DateTime? PoolWarmedAt { get; private set; }
    // CreatedAt / UpdatedAt / RowVersion — CouponEvent 와 동일
}
```

`StatusAt` / `IsStatus` 는 `CouponEvent` 의 것을 그대로 복사하지 말고 **공통 추상으로 올린다** (`IScheduledEvent` 또는 owned type). 규칙이 두 곳에 있으면 한쪽만 고치는 사고가 난다 — 이미 `StatusAt`/`IsStatus` 쌍에서 겪는 문제를 셋으로 늘릴 이유가 없다.

### DrawPrize

```csharp
public class DrawPrize
{
    public long Id { get; private set; }
    public long DrawEventId { get; private set; }

    public string Name { get; private set; }           // "전설 무기 상자"
    public int SlotIndex { get; private set; }         // 돌림판 화면상 위치 (0..N-1)

    public long ItemId { get; private set; }           // 지급 아이템
    public int ItemQty { get; private set; }

    /// <summary>정수 가중치. 확률은 weight / totalWeight 로 파생한다. 결정 1 참조.</summary>
    public int Weight { get; private set; }

    /// <summary>남은 재고. -1 = 무제한.</summary>
    public int InitialStock { get; private set; }

    /// <summary>소진 시 대체할 경품. SoldOutPolicy.Fallback 일 때 필수.</summary>
    public long? FallbackPrizeId { get; private set; }

    /// <summary>잭팟 표시. 2인 승인·이상탐지·알림의 트리거.</summary>
    public bool IsJackpot { get; private set; }

    /// <summary>true = 꽝. 지급 없음. 천장 카운터를 증가시킨다.</summary>
    public bool IsBlank { get; private set; }
}
```

### DrawWeightVersion — 가중치는 UPDATE 하지 않는다 ★

```csharp
public class DrawWeightVersion
{
    public long Id { get; private set; }
    public long DrawEventId { get; private set; }
    public int Version { get; private set; }           // 1, 2, 3...

    /// <summary>스냅샷. [{prizeId, weight, stock, itemId, qty}, ...] 직렬화.</summary>
    public string SnapshotJson { get; private set; }

    /// <summary>스냅샷의 SHA-256. 로그에 이 값을 남기면 위변조를 탐지할 수 있다.</summary>
    public string SnapshotHash { get; private set; }

    public int TotalWeight { get; private set; }
    public DateTime ActivatedAt { get; private set; }
    public DateTime? DeactivatedAt { get; private set; }
    public long CreatedByAdminId { get; private set; }
    public string ChangeReason { get; private set; }   // 필수
}
```

**진행 중인 이벤트의 확률을 운영자가 바꾸면, 그전에 일어난 추첨을 설명할 수 없게 된다.**
그래서 가중치 변경은 UPDATE 가 아니라 **새 버전 INSERT + 활성 버전 전환**이다(append-only).
`DrawLog` 는 자기가 쓴 버전을 가리키므로, 6개월 뒤 CS 클레임이 와도 **그 시점의 확률표를 정확히 꺼낼 수 있다.**

이력 테이블이 없으면 이런 대화가 된다 — *"유저는 0.5%라고 들었다는데 지금 DB엔 0.1%로 적혀 있습니다."* 답이 없다.

### DrawLog — 재현 가능한 추첨 이력 ★

```csharp
public class DrawLog
{
    public long Id { get; private set; }
    public Guid RequestId { get; private set; }        // UNIQUE. 멱등 키
    public long DrawEventId { get; private set; }
    public string UserId { get; private set; }
    public DrawResult Result { get; private set; }

    // ── 재현에 필요한 최소 집합 ──────────────────────────────
    public long WeightVersionId { get; private set; }  // 그때의 확률표
    public long RandomValue { get; private set; }      // 뽑은 난수 원본
    public int TotalWeight { get; private set; }       // 그때의 가중치 합
    public int Roll { get; private set; }              // randomValue % totalWeight
    // ────────────────────────────────────────────────────────

    public long? PrizeId { get; private set; }         // 최종 결정된 경품
    public long? OriginalPrizeId { get; private set; } // 치환 전 (fallback 적용 시)
    public bool FallbackApplied { get; private set; }
    public bool PityApplied { get; private set; }

    public long? ItemId { get; private set; }          // 비정규화 — 이력 조회에 조인 제거
    public int ItemQty { get; private set; }
    public string? PrizeName { get; private set; }

    public int TicketsSpent { get; private set; }
    public int PityCountAfter { get; private set; }

    public DateTime RequestedAt { get; private set; }
    public DateTime PersistedAt { get; private set; }
    public int PersistenceLagMs { get; private set; }
    public string? FailureDetail { get; private set; }
}
```

**`(WeightVersionId, RandomValue)` 두 개면 추첨을 그대로 재계산할 수 있다.**
이게 이 설계에서 가장 중요한 한 줄이다. CS 클레임, 내부 감사, 규제 대응, 버그 조사가 전부 여기서 끝난다.

검증 함수 하나를 어드민에 노출한다:

```
POST /admin/draws/{logId}/verify
→ { stored: prizeId=7, recomputed: prizeId=7, match: true }
```

### 열거형

```csharp
public enum DrawResult : byte
{
    Won = 1,              // 당첨 (꽝 포함 — "추첨이 정상 수행됨")
    OutOfPeriod = 3,
    DailyLimitExceeded = 4,
    DuplicateRequest = 5,
    Suspended = 6,
    InsufficientTicket = 7,
    AllPrizesSoldOut = 8,   // fallback 도 소진된 극단 상황
    SystemError = 99,
}

public enum SoldOutPolicy : byte { Fallback = 0, Renormalize = 1 }
```

`IssueResult` 와 값을 맞춰둔다(3~6은 같은 의미). 운영툴의 필터 UI를 공유하기 위함이다.

---

## 3. Redis 키 설계

```
{drw:N}:meta          HASH    startsAtMs, endsAtMs, suspended, dailyLimit,
                              ticketItemId, ticketCost, policy, pityThreshold,
                              pityPrizeId, weightVersionId, totalWeight, prizeCount
{drw:N}:cum           LIST    누적 가중치 — "prizeId|cumWeight|fallbackId|isBlank"
                              워밍업 시 한 번 만들고 이벤트 내내 불변 (결정 2)
{drw:N}:stock         HASH    prizeId -> 남은 재고 (-1 = 무제한)
{drw:N}:daily:<ymd>   HASH    userId -> 오늘 추첨 횟수      (TTL 48h)
{drw:N}:pity          HASH    userId -> 연속 미당첨 횟수
{drw:N}:tickets       HASH    userId -> 잔여 티켓
{drw:N}:req:<rid>     STRING  멱등 키 "result|prizeId|itemId|qty"
{drw:N}:drawn         STREAM  DB 적재용 아웃박스
{drw:N}:won:<pid>     STRING  경품별 당첨 카운터 (모니터링·편차 검정용)
```

전부 `{drw:N}` 해시태그를 쓴다 — Redis Cluster 에서 한 슬롯에 모이지 않으면 스크립트가 여러 키를 못 만진다.
쿠폰의 `{evt:N}` 과 접두사를 나눈 이유는, 같은 Redis 에서 두 이벤트 종류가 공존해도 키가 섞이지 않게 하기 위함이다.

### 누적 가중치를 워밍업에 만드는 이유

```
prizes:  A(w=5)  B(w=45)  C(w=200)  D(w=750)     totalWeight = 1000
cum:     A|5     B|50     C|250     D|1000
```

`roll = rand % 1000` 을 앞에서부터 스캔해 `roll < cum` 인 첫 항목이 당첨.
슬롯이 보통 6~12개라 선형 스캔으로 충분하고, 이분 탐색은 가독성만 해친다.

**(b) Fallback 정책에서는 이 배열이 불변**이므로 매 추첨마다 만들 필요가 없다. 이게 결정 2에서 (b)를 고른 구현상 이유다.

---

## 4. 추첨 Lua 스크립트

기존 `issue_coupon.lua` 의 **두 원칙을 그대로 계승**한다.

> **원칙 1** 멱등 검사가 가장 먼저다.
> **원칙 2** 읽기 검증을 전부 끝낸 뒤에야 쓰기를 시작한다 (Redis Lua 는 롤백이 없다).

여기에 룰렛 고유의 원칙 하나가 추가된다.

> **원칙 3** 티켓 차감은 쓰기 구간의 **맨 앞**, 그리고 그 뒤로는 실패 분기가 없어야 한다.
> "티켓은 빠졌는데 아무것도 못 받았다" 는 상태가 유저 입장에서 가장 나쁜 실패다.
> 재고 판정·fallback 치환을 전부 읽기 단계에서 끝내는 이유가 이것이다.

```lua
--[[
  KEYS[1] {drw:N}:meta     KEYS[2] {drw:N}:cum       KEYS[3] {drw:N}:stock
  KEYS[4] {drw:N}:daily    KEYS[5] {drw:N}:pity      KEYS[6] {drw:N}:tickets
  KEYS[7] {drw:N}:req:<rid> KEYS[8] {drw:N}:drawn

  ARGV[1] userId       ARGV[2] requestId   ARGV[3] nowMs
  ARGV[4] randomValue  ARGV[5] reqTtl      ARGV[6] logFailure

  반환: { result, prizeId, itemId, qty, roll, fallbackApplied,
          pityApplied, pityCountAfter, remainingTickets, priorResult }
]]

-- ── 1. 멱등 검사 (원칙 1) ───────────────────────────────────────
-- 재시도는 같은 경품을 돌려받는다. 다시 돌려서 다른 결과가 나오면
-- 네트워크 타임아웃마다 유저가 재추첨 기회를 얻는 셈이 된다.
local prior = redis.call('GET', KEYS[7])
if prior then return replay(prior) end

-- ── 2. 메타 로드 ────────────────────────────────────────────────
-- 기간·중단 판단을 앱에서 하면 판단과 차감 사이에 운영자가 중단할 수 있다(TOCTOU).

-- ── 3. 거부 검증 (전부 읽기 — 원칙 2) ───────────────────────────
--   중단 → 기간 → 일일 횟수 → 티켓 잔액
-- 거부 범위가 넓고 싼 것부터. 티켓 확인을 마지막에 두는 이유는
-- 다른 사유로 거부될 요청이 티켓 조회까지 가지 않게 하기 위함.

-- ── 4. 천장 판정 (읽기) ─────────────────────────────────────────
-- pityThreshold > 0 이고 연속 미당첨이 임계 도달이면 슬롯을 강제 지정한다.
-- 난수를 쓰지 않으므로 로그에 pityApplied=true 로 남겨 재현 시 분기를 맞춘다.

-- ── 5. 슬롯 결정 (읽기, 순수 계산) ──────────────────────────────
-- roll = randomValue % totalWeight
-- cum 리스트를 앞에서 스캔 → roll < cumWeight 인 첫 항목

-- ── 6. 재고 판정 · fallback 치환 (읽기) ─────────────────────────
-- stock == 0 이면 fallbackPrizeId 로 치환하고 fallbackApplied=true.
-- 치환 대상도 소진이면 한 번 더(최대 2단계). 그래도 없으면 AllPrizesSoldOut.
-- ★ 여기까지 아무것도 쓰지 않았다. 지금 거부해도 잃는 것이 없다.

-- ── 7. 확정 (여기서부터 쓰기 — 원칙 2·3) ────────────────────────
--   7-1. 티켓 차감        HINCRBY tickets userId -cost
--   7-2. 재고 차감        HINCRBY stock prizeId -1   (무제한이면 skip)
--   7-3. 천장 갱신        꽝이면 +1, 당첨이면 0으로 리셋
--   7-4. 일일 카운터      HINCRBY daily userId 1  (+ EXPIRE)
--   7-5. 당첨 카운터      INCR won:<prizeId>
--   7-6. 멱등 키          SET req:<rid> "..." EX ttl
--   7-7. 아웃박스         XADD drawn * ...
-- 7-1 이후로 실패 분기가 없다. 모든 판정이 6단계에서 끝났기 때문이다.
```

### 재고 차감에 `HINCRBY` 를 쓰고 사후 검사하지 않는 이유

쿠폰의 `LPOP` 처럼 "확인과 차감을 겸하는" 연산이 여기엔 없다.
대신 **스크립트 전체가 원자적**이므로 6단계에서 읽은 재고 값이 7단계까지 유효하다 — 그 사이에 다른 요청이 끼어들 수 없다.
이것이 Lua 를 쓰는 이유 그 자체이고, 쿠폰 문서 2장의 논거와 동일하다.

---

## 5. 티켓/재화 차감 — PoC 범위와 실무의 차이

**PoC 범위:** 티켓 잔액을 Redis(`{drw:N}:tickets`)에 두고 DB 원장은 비동기로 따라온다. 추첨과 차감이 한 스크립트 안이므로 원자적이다.

**실무에서 유료 재화라면 이 구조를 쓸 수 없다.** 재화는 보통 별도 서버(결제/계정 도메인)가 소유하고, 그 사이에는 분산 트랜잭션이 없다. 표준 해법은 **예약(reserve) → 확정(commit) / 취소(cancel) 사가**다.

```
1. POST /currency/reserve   { userId, amount, reserveId }   → 재화 서버가 홀드
2. POST /draw (Lua)         추첨 수행, reserveId 기록
3-a. 성공 → POST /currency/commit  { reserveId }
3-b. 실패 → POST /currency/cancel  { reserveId }
3-c. 응답 유실 → 타임아웃 후 재화 서버가 자동 해제 (reserve 에 TTL)
```

핵심은 **3-c**다. 예약에 TTL 이 없으면 프로세스가 죽는 순간 유저 재화가 영구히 묶인다.
문서에 명시해 두는 이유는, PoC 구조를 그대로 실서비스에 옮기면 안 되는 지점이기 때문이다.

---

## 6. 확률 공시

### 공개 페이지

`GET /events/{code}/odds` — 로그인 불필요. 현재 활성 `DrawWeightVersion` 에서 **자동 생성**한다.

| 경품 | 수량 | 확률 |
|---|---:|---:|
| 전설 무기 상자 | 1 | 0.50% |
| 영웅 무기 상자 | 1 | 4.50% |
| 강화 주문서 | 5 | 20.00% |
| 골드 | 1,000 | 75.00% |

**어드민에서 확률을 바꾸면 이 페이지가 자동으로 바뀐다.** 수기 동기화는 금지 — 실무 사고의 단골이다.
(어드민 확률표와 공시 페이지가 다른 소스를 보는 순간, 언젠가 반드시 어긋난다.)

### 반올림으로 합이 100%가 안 되는 문제

소수 둘째 자리로 자르면 합이 99.99% 나 100.01% 가 된다. 세 가지 중 하나를 고른다.

1. **잔차를 가중치 최대 항목에 흡수** (권장) — 가장 큰 항목이 0.01% 틀려도 표시상 무해
2. 표기 자릿수를 늘려 잔차를 0으로 — 0.0001% 같은 숫자가 나와 가독성이 나쁨
3. "그 외" 행에 몰아넣기 — 항목이 많을 때만 유효

어느 쪽이든 **합계 행을 반드시 표시하고, 그 값이 100.00% 가 되게 한다.** 합이 99.98% 로 보이는 공시는 그 자체로 문의를 만든다.

### 소진 정책 문구

`SoldOutPolicy` 에 따라 공시 하단 문구가 달라진다.

- **Fallback**: "한정 경품 소진 시 해당 확률은 *골드 1,000* 지급으로 대체됩니다."
- **Renormalize**: "한정 경품 소진 시 잔여 경품의 확률이 재산정됩니다. 재산정된 확률은 본 페이지에 실시간 반영됩니다."

Renormalize 를 고른다면 **공시 페이지가 실시간 재고를 반영해야 한다.** 이게 (a) 정책의 진짜 비용이다.

---

## 7. API

```http
POST /api/draws/{drawEventId}/spin
Content-Type: application/json

{ "userId": "player-1234", "requestId": "3f2b...(선택)" }
```

```jsonc
// 200 OK
{
  "result": "Won",
  "prize": {
    "prizeId": 7, "slotIndex": 3, "name": "강화 주문서",
    "itemId": 3001, "qty": 5, "isJackpot": false, "isBlank": false
  },
  "fallbackApplied": false,
  "pityApplied": false,
  "pityCount": 0,
  "remainingTickets": 4,
  "remainingDraws": 2,          // 오늘 남은 횟수
  "requestId": "3f2b...",
  "latencyMs": 8
}
```

| 상태 코드 | 조건 |
|---|---|
| 200 | `Won`, `DuplicateRequest` (멱등 재생 — 원하던 상태가 이미 달성됨) |
| 400 | 잘못된 요청 |
| 409 | `InsufficientTicket`, `DailyLimitExceeded` |
| 410 | `OutOfPeriod`, `Suspended`, `AllPrizesSoldOut` |
| 503 | Redis 장애 — **fail-fast**. 쿠폰과 동일한 판단 |

Redis 장애 시 DB 로 우회하지 않는 이유는 쿠폰과 같다. 재고의 원천이 둘이 되면 "정확히 N개"가 깨지고, **이미 지급된 아이템은 회수할 수 없다.**

### 클라이언트 연출과 서버 확정의 분리

돌림판 애니메이션은 클라이언트가 재생하지만, **결과는 API 응답이 이미 확정한 값이다.**
클라이언트는 `slotIndex` 를 받아 그 자리에 멈추는 애니메이션을 역산해 재생한다. 그 반대가 되면(클라가 돌리고 결과를 서버에 통보) 메모리 조작으로 원하는 경품을 지정할 수 있다.

**연출 중 앱이 죽어도 결과는 이미 서버에 남아 있다.** 재접속 시 `GET /api/draws/{id}/logs?userId=…&limit=1` 로 마지막 결과를 보여주면 된다 — 이게 우편함 지급이 필요한 또 다른 이유다.

---

## 8. 지급 — 우편함(Mailbox) 경유

추첨 결과를 인벤토리에 직접 꽂지 않는다.

```
추첨 확정(Redis) → 아웃박스 스트림 → 워커 → DrawLog + 우편함 INSERT (같은 트랜잭션)
                                            → 유저가 수령 → 인벤토리 반영
```

이유 넷.

1. 인벤토리 꽉 참 / 오프라인 유저 처리
2. 잘못 설정된 경품을 **미수령분만 회수** 가능
3. 유저에게 "받았다"는 명시적 확인 UX — CS 문의의 절반이 여기서 사라진다
4. 추첨(빠른 경로)과 인벤토리 반영(느린 경로)의 분리 — 쿠폰의 비동기 적재와 같은 구조

PoC 에서는 우편함 테이블(`DrawRewardMails`)까지만 만들고 인벤토리 연동은 스텁으로 둔다.

---

## 9. 운영툴

기존 운영툴에 화면을 얹는다. 쿠폰 화면의 관례(상태 필터, 3중 확인 중단, 감사 로그)를 그대로 따른다.

### 9-1. 룰렛 이벤트 생성 — 슬롯 편집기

| 슬롯 | 경품명 | 아이템 | 수량 | 가중치 | 재고 | 확률 | 잭팟 | 소진 시 대체 |
|---:|---|---:|---:|---:|---:|---:|:---:|---|
| 0 | 전설 무기 상자 | 9001 | 1 | 5 | 10 | **0.50%** | ✔ | 골드 |
| 1 | 영웅 무기 상자 | 9002 | 1 | 45 | 100 | **4.50%** | | 골드 |
| 2 | 강화 주문서 | 3001 | 5 | 200 | -1 | **20.00%** | | — |
| 3 | 골드 | 1001 | 1000 | 750 | -1 | **75.00%** | | — |
| | | | **합계** | **1000** | | **100.00%** | | |

- 가중치를 입력하면 **확률이 실시간으로 계산돼 보인다.** 운영자는 확률로 사고하고 시스템은 정수로 저장한다.
- 잭팟 슬롯에 `FallbackPrizeId` 가 비어 있으면 **저장을 막는다** (정책이 Fallback 일 때).
- 재고 `-1` 은 무제한. UI 에서는 `∞` 로 표시.

### 9-2. 확률 시뮬레이터 ★ 오타 방어의 핵심

```
[ 10,000 회 시뮬레이션 ]

전설 무기 상자    기댓값 50.0회 →  예상 지급 50개   (재고 10개 → 40회 대체 발생)
영웅 무기 상자    기댓값 450.0회 →  예상 지급 450개  (재고 100개 → 350회 대체 발생) ⚠
강화 주문서      기댓값 2,000.0회 → 예상 지급 10,000개
골드             기댓값 7,500.0회 → 예상 지급 7,890,000골드

⚠ 예상 지급 규모가 재고를 크게 초과합니다. 재고 또는 가중치를 재검토하세요.
```

**가중치 `5` 를 `50` 으로 잘못 친 사고는 이 화면에서만 걸린다.**
저장 전에 반드시 통과해야 하는 단계로 둔다 — 건너뛸 수 있는 정보성 화면이 아니라 게이트다.

### 9-3. 2인 승인 (maker-checker)

다음 중 하나라도 해당하면 등록자와 다른 편집자의 승인 없이는 활성화되지 않는다.

- `IsJackpot` 슬롯이 하나라도 있음
- 시뮬레이션 기대 지급 가치가 임계값 초과
- **진행 중** 이벤트의 가중치 변경 (= 새 `DrawWeightVersion` 활성화)

세 번째가 특히 중요하다. 진행 중 확률 변경은 공시와 직결되므로 혼자 누를 수 있으면 안 된다.

### 9-4. 실시간 현황 — 기대값 대비 편차

3초 폴링(쿠폰 상세와 동일한 판단 — WebSocket 을 쓰지 않는 이유도 같다).

| 경품 | 당첨 수 | 기댓값 | 편차 | 재고 |
|---|---:|---:|---:|---:|
| 전설 무기 상자 | 3 | 4.2 | −29% | 7 / 10 |
| 영웅 무기 상자 | 41 | 37.8 | +8% | 59 / 100 |
| 강화 주문서 | 172 | 168.0 | +2% | ∞ |
| 골드 | 624 | 630.0 | −1% | ∞ |

**카이제곱 적합도 검정**을 돌려 유의수준을 넘으면 경고를 띄운다.
편차가 지속적으로 크다는 건 (1) 가중치 설정 오류 (2) 워밍업 데이터 불일치 (3) 어뷰징 중 하나다 — 셋 다 즉시 알아야 하는 사건이다.

### 9-5. 추첨 이력 + 재현 검증

쿠폰 발급 이력과 같은 구조. 추가로 각 행에 **[재현 검증]** 버튼:

```
DrawLog #48213
  저장된 결과:  prizeId=7 (강화 주문서 x5)
  재계산 결과:  prizeId=7 (강화 주문서 x5)
  weightVersion=3 (hash 4f2a…)  randomValue=8472039481  roll=372
  ✔ 일치
```

### 9-6. 이상 탐지

- 동일 유저가 잭팟 N회 이상 → 알림
- 특정 IP 대역에서 추첨 집중 → 알림
- 짧은 시간 내 동일 유저 추첨 폭주 → 레이트 리밋 + 알림

---

## 10. 테스트 전략

기존 통합 테스트(실제 MSSQL·Redis) 방식을 그대로 확장한다.

### 정확성

| 테스트 | 단언 |
|---|---|
| 재고 초과 지급 0건 | 재고 10인 잭팟을 3,000 VU 로 10만 회 추첨 → 당첨 정확히 10회 |
| 멱등성 | 같은 `requestId` 100회 재시도 → 같은 `prizeId`, 티켓 1회만 차감 |
| **재현성** | 이력의 `(weightVersionId, randomValue)` 로 재계산 → 저장된 `prizeId` 와 일치 |
| 티켓 원자성 | 티켓 1개로 동시 10요청 → 성공 1건, 차감 1회 |
| 천장 | `PityThreshold=10` 에서 9회 꽝 후 10회차 → `pityApplied=true`, 지정 경품 |
| 일일 한도 | 리셋 시각 경계(03:59:59 / 04:00:00) 전후 카운터 |
| 전 슬롯 소진 | `AllPrizesSoldOut` 반환, **티켓 차감되지 않음** |

마지막 줄이 원칙 3의 검증이다.

### 통계

```csharp
[Fact]
public void 가중치_분포가_카이제곱_검정을_통과한다()
{
    // 100만 회 추첨 → 관측 빈도 vs 기대 빈도
    // 자유도 3, 유의수준 0.01 → 임계값 11.34
    Assert.True(chiSquare < 11.34, $"χ²={chiSquare} — 분포가 가중치와 다릅니다");
}
```

> 통계 검정은 본질적으로 **확률적으로 실패**한다(유의수준 1%면 100번에 1번). CI 에서 간헐적 실패를 만들지 않도록
> **고정 시드**로 돌리고, 시드 없는 무작위 검정은 별도 nightly 잡으로 분리한다.

### 부하

쿠폰과 같은 조건(200 / 1,000 / 3,000 VU)으로 측정해 **쿠폰 발급 경로와 나란히 비교표에 올린다.**
룰렛은 스크립트가 더 길고 키를 더 많이 만지므로 처리량이 얼마나 떨어지는지가 의미 있는 수치다.

---

## 11. 구현 현황

| 단계 | 범위 | 상태 |
|---|---|:--:|
| **1** | 도메인 + 스키마 + 마이그레이션 | ✅ |
| **2** | 워밍업 + Lua 추첨 스크립트 + API | ✅ |
| **3** | 아웃박스 워커 + 우편함 적재 + 이력 | ✅ |
| **4** | 천장 · 일일 한도 · 티켓 | ✅ |
| **5** | 어드민 — 목록 · 슬롯 편집기 · 시뮬레이터 게이트 · 상세 · 이력 · 재현 검증 | ✅ |
| **6** | 확률 공시 페이지 + 편차(카이제곱) 모니터링 | ✅ |
| **7** | 2인 승인 워크플로 · 부하 비교 측정 · 우편 수령 API | 미구현 |

### 구현된 것의 지도

| 관심사 | 위치 |
|---|---|
| 추첨 원자 처리 | `Infrastructure/Redis/Scripts/draw_spin.lua` |
| 스토어 · 키 규약 | `RedisDrawStore`, `RedisKeys.Draw` |
| 도메인 규칙 · 검증 | `Domain/DrawEvent.cs` (`ValidateInput`, `ValidatePrizes`) |
| 확률 표기 (단일 진실) | `Application/DrawOdds.cs` — 어드민과 공시가 공유 |
| 스냅샷 · 재현 검증 | `Application/DrawSnapshot.cs` (`Recompute`) |
| 시뮬레이터 게이트 | `Application/DrawSimulator.cs` + `Pages/Draws/Create` |
| 편차 검정 | `Application/DrawDeviation.cs` |
| 비동기 적재 · 우편함 | `Infrastructure/Workers/DrawPersistenceWorker.cs` |
| 추첨 API | `POST /api/draws/{id}/spin` |
| 유저 상태 · 공시 | `GET /api/draws/{id}/users/{userId}`, `GET /api/draws/{id}/odds`, `/Odds?code=` |

### 남은 것

- **2인 승인(maker-checker)** — 잭팟 슬롯·진행 중 확률 변경에 등록자와 다른 편집자의
  승인을 요구한다. 승인 엔티티와 대기 상태가 필요해 별도 작업으로 뒀다.
  현재는 시뮬레이터가 경고만 띄운다.
- **부하 비교 측정의 수치** — 하네스(`loadtest/draw-spike.js`, `provision-draw.sh`,
  `run-draw.sh`)는 만들었고 실행마다 초과 지급 0건을 자체 확인한다.
  수치는 실제로 돌려서 채워야 하는 자리다 — docs/load-test.md 마지막 절 참조.
- **우편 수령 API** — 우편함 테이블과 적재까지는 구현했고, 수령 시 인벤토리 반영은
  게임 서버의 몫이라 스텁으로 뒀다.
- **이상 탐지** — 같은 유저의 잭팟 연속 당첨, IP 집중. 인덱스(`DrawEventId, PrizeId,
  RequestedAt` 필터드)는 이미 그 질의를 염두에 두고 깔아 뒀다.

---

## 12. 확정된 결정 (실무 기본값)

착수 전 열어 뒀던 다섯 가지를 업계에서 가장 흔한 선택으로 확정했다.

| 결정 | 선택 | 근거 |
|---|---|---|
| 소진 정책 | **Fallback** | 공시 확률 = 실행 확률이 항상 유지된다. 재분배는 코드에서 아예 거부한다 |
| 티켓 소유권 | 이 시스템이 소유 (Redis + 감사 로그) | PoC 범위. 외부 재화 서버라면 5장의 reserve-commit 사가가 먼저다 |
| 천장 | **포함** — 연속 꽝 기준, N번째가 확정 | "10회 천장" 이 11번째를 뜻하면 유저는 속았다고 느낀다 |
| 지급 경로 | **우편함 경유** | 미수령분 회수 가능 · 오프라인 유저 · 인벤토리 포화 |
| 공시 범위 | 전체 경품, 익명 공개 | 비용은 페이지 하나, 효과는 문의 감소 |

### 이 구현이 보증하는 것 (통합 테스트)

| 성질 | 테스트 |
|---|---|
| 재고를 넘긴 지급이 없다 | 동시 400요청 · 재고 50 → 정확히 50건 |
| 티켓이 음수가 되지 않는다 | 티켓 1개로 동시 10요청 → 성공 1건 |
| 실패는 티켓을 건드리지 않는다 | 잔액 부족 거부 후 잔액 불변 |
| 재시도는 재추첨이 아니다 | 같은 RequestId → 같은 경품, 차감 없음 |
| 천장은 N번째에 확정된다 | 꽝 9회 뒤 10회차 당첨 |
| 이력만으로 추첨을 재현할 수 있다 | (난수, 확률표) 재계산 = 저장된 결과 |
| 확률 변경이 과거를 지우지 않는다 | 이전 이력이 이전 버전을 계속 가리킨다 |
| 분포가 가중치와 일치한다 | 카이제곱 검정 (시드 고정 — 결정적) |
| 표시 확률의 합은 100.00% | 반올림 잔차 보정 |
| 시뮬레이션 없이 생성할 수 없다 | 게이트 우회 시도 → 거부 |
| 공시는 로그인 없이 열린다 | 익명 클라이언트로 확인 |

---

## 부록 — 재현 검증 구현

```csharp
/// <summary>
/// 저장된 이력으로 추첨을 재계산한다. 난수를 앱에서 뽑아 로그에 남긴 덕분에 가능하다(결정 3).
/// 이 함수가 성립하지 않는 설계는 사후 검증이 불가능한 설계다.
/// </summary>
public static long Recompute(DrawWeightVersion version, long randomValue, bool pityApplied, long? pityPrizeId)
{
    if (pityApplied) return pityPrizeId!.Value;   // 천장은 난수를 쓰지 않는다

    var prizes = JsonSerializer.Deserialize<PrizeSnapshot[]>(version.SnapshotJson)!;
    var roll = (int)(randomValue % (ulong)version.TotalWeight);

    var cum = 0;
    foreach (var p in prizes)
    {
        cum += p.Weight;
        if (roll < cum) return p.PrizeId;
    }
    throw new InvalidOperationException("누적 가중치가 totalWeight 에 도달하지 못했습니다 — 스냅샷 손상");
}
```

`FallbackApplied` 가 true 인 이력은 `OriginalPrizeId` 와 비교한다 — 재계산은 **치환 전** 슬롯을 돌려주므로, 치환 자체는 당시 재고에 의존해 재현되지 않는다. 그래서 `OriginalPrizeId` 를 따로 저장한다.

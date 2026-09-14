# 2단계 — 발급 API와 동시성 제어

이 PoC 의 핵심. **동시 요청이 아무리 몰려도 발급 수가 설정 수량을 넘지 않는다**를 실행 가능한 테스트로 증명한다.

---

## API

```
POST /api/events/{eventId}/coupons/issue
Content-Type: application/json

{ "userId": "player-1234", "requestId": "e5f1…" }   // requestId 는 선택
```

```jsonc
// 200 OK
{ "result": "Success", "couponCode": "K7MJQ2XTVB9D", "remaining": 4821,
  "priorResult": null, "requestId": "e5f1…", "latencyMs": 1 }

// 409 Conflict — 요청은 유효하나 현재 상태에서 거부
{ "result": "SoldOut", "couponCode": null, "remaining": -1, … }

// 200 OK — 멱등 재생. 클라이언트가 원하던 상태는 이미 달성돼 있다.
{ "result": "DuplicateRequest", "couponCode": "K7MJQ2XTVB9D", "priorResult": "Success", … }
```

| 결과 | HTTP | 의미 |
|---|---|---|
| `Success` | 200 | 발급 완료 |
| `DuplicateRequest` | 200 | 같은 `requestId` 재시도. `priorResult` 에 최초 결과, `couponCode` 에 최초 쿠폰 |
| `SoldOut` | 409 | 수량 소진 |
| `OutOfPeriod` | 409 | 이벤트 기간 외 |
| `LimitExceeded` | 409 | 유저당 한도 초과 |
| `Suspended` | 409 | 운영자 강제 중단 |
| `SystemError` | 503 | 워밍업되지 않은 이벤트 등 |

`requestId` 를 보내지 않으면 서버가 생성한다. 이 경우 **재시도 멱등성은 보장되지 않는다**
(매 요청이 새 키가 된다). 네트워크 타임아웃 후 재시도하는 클라이언트는 직접 생성해 보내야 한다.

---

## 동시성 제어 — 왜 Lua 인가

재고 확인과 차감을 따로 하면(`GET` → 판단 → `DECR`) 두 명령 사이에 다른 요청이 끼어든다.
`DECR` 만 쓰면 원자적이긴 하나 재고가 음수로 내려가 **초과 발급**이 된다.
Lua 스크립트는 Redis 에서 단일 명령처럼 원자적으로 실행되므로, 그 안에서는
"확인 후 차감"이 안전하다. 재고·유저 한도·멱등 키를 한 번에 다루려면 이 원자성이 필요하다.

스크립트는 [`issue_coupon.lua`](../src/CouponOps.Web/Infrastructure/Redis/Scripts/issue_coupon.lua)
에 있고 각 단계의 **순서 근거**가 주석으로 달려 있다. 요약하면 두 원칙이다.

**원칙 1 — 멱등 검사가 가장 먼저다.**
재시도가 재고를 두 번 깎으면 안 되므로 다른 검사보다 앞이어야 한다.
기간 검사를 먼저 두면 "이미 발급받은 유저가 종료 후 재시도" 가 `OutOfPeriod` 로 응답되어
최초 결과와 달라진다 — 멱등성이 깨진다.

**원칙 2 — 읽기 검증을 전부 끝낸 뒤에야 쓰기를 시작한다.**
Redis 는 Lua 실행 중 오류가 나도 **앞서 실행된 명령을 되돌리지 않는다.** 롤백이 없다.
검증과 쓰기를 섞으면 뒤쪽 검증에서 거부될 때 앞에서 깎은 재고가 그대로 증발한다.

예외가 하나 있다. `LPOP` 은 쓰기지만 실패 시(nil) 아무것도 바꾸지 않으므로 되돌릴 것이 없다.
그래서 첫 번째 쓰기 자리에 둔다. 사전 생성 모드에서 `LPOP` 은 **재고 판정과 코드 배정을 겸하므로**
"재고는 깎였는데 코드가 없다"는 상태가 구조적으로 생기지 않는다.

### 실행 순서

```
1. 멱등 검사       GET  {evt:N}:req:<rid>      → 있으면 최초 결과 재생
2. 메타 로드       HMGET {evt:N}:meta          → 없으면 SystemError
3. 거부 검증(읽기)  중단 → 기간 → 유저 한도
4. 코드 확보       LPOP {evt:N}:pool           (또는 GET/DECR stock + INCR seq)
5. 발급 확정(쓰기)  HINCRBY users / SET req / XADD issued
```

기간·중단 검사를 **앱이 아니라 스크립트 안에서** 하는 이유는 TOCTOU 때문이다.
앱이 "진행중" 이라고 판단한 뒤 스크립트를 호출하는 사이에 운영자가 이벤트를 중단할 수 있다.

---

## Redis 장애 시 — fail-fast를 선택한 이유

**DB 로 우회(fallback)하지 않고 503 으로 거부한다.**

재고의 원천이 둘이 되는 순간 "정확히 N개" 를 보증할 수 없기 때문이다.
Redis 가 끊긴 사이 DB 경로로 나간 수량은 Redis 카운터에 반영되지 않고, 복구된 Redis 는
자기가 아는 재고로 계속 발급한다. 결과는 초과 발급이고, 이 PoC 가 증명하려는 성질이
정확히 그 지점에서 깨진다.

비용이 비대칭적이다 — **초과 발급은 보상이 어렵다**(이미 유저 손에 코드가 있다).
반면 짧은 발급 거부는 재시도로 회복된다. 그래서 거부를 택한다.
`503 + Retry-After` 로 재시도 가능함을 클라이언트에 명시한다.

> 기동 시점은 다르게 다룬다. `AbortOnConnectFail = false` 로 두어 Redis 가 아직 안 떴다고
> 앱까지 죽지는 않게 한다(컨테이너 기동 순서는 보장되지 않는다). 요청 처리 중의 연결 실패만
> fail-fast 대상이다.

---

## DB 쓰기 분리

발급이 확정되면 Lua 가 `{evt:N}:issued` **스트림**에 이력을 넣고 API 는 즉시 응답한다.
`IssuancePersistenceWorker` 가 소비자 그룹으로 이를 읽어 배치로 DB 에 적재한다.

- **DB 적재가 늦거나 멈춰도 발급의 정확성에는 영향이 없다.** 영향받는 것은 운영툴에 보이는
  이력의 신선도뿐이다.
- 소비는 at-least-once 다. 같은 항목이 두 번 올 수 있으므로
  `IssuanceLogs.RequestId` UNIQUE 제약과 워커의 선필터가 중복 행을 막는다.
- 쿠폰 상태 변경과 이력 적재는 **한 트랜잭션**이다. 쿠폰만 반영되고 이력이 빠지면
  재처리 시 선필터가 걸러주지 못한다.
- 워커가 적재 도중 죽으면 해당 항목은 PEL 에 남아 `>` 로는 다시 오지 않는다.
  재기동 시 자기 PEL 을 한 번 회수(`reclaimOwn`)해 처리한다.

> **남은 과제**: 다른 워커 인스턴스가 죽은 채 남긴 PEL 은 `XAUTOCLAIM` 으로 회수해야 한다.
> 단일 인스턴스 PoC 라 구현하지 않았다.

---

## 검증

```bash
bash scripts/dev-setup.sh          # .NET SDK · Docker · 테스트 이미지 준비
dotnet test
```

Testcontainers 가 MSSQL 과 Redis 를 실제로 띄운다. 인메모리 대역이나 목이 아니다.
동시 요청은 `TaskCompletionSource` 게이트로 전원을 출발선에 세운 뒤 한 번에 푼다 —
그냥 `Task.WhenAll` 로 묶으면 순차적으로 출발해 실제 경합이 거의 일어나지 않는다.

### 결과 (13 passed / 0 failed, 1.9분)

| 테스트 | 결과 |
|---|---|
| 동시 요청 **1000건 / 재고 100** | 성공 **100**, 소진거부 **900**, 코드 중복 0, 풀 잔여 0 |
| **초과 발급 0건** (2000요청 / 재고 50) | 발급 50, **초과 0**, 중복코드 0, Redis 발급기록 50 |
| 동일 유저 50회 동시 (한도 3) | 성공 **3**, 한도초과 47 |
| 파생(Derived) 모드 800요청 / 재고 80 | 발급 **80**, 코드 전부 유일, 재고 카운터 0 (음수 없음) |
| 동일 RequestId 재시도 | `DuplicateRequest` + 최초와 **동일한 쿠폰 코드** |
| 재시도 20회가 재고를 추가 소모하지 않음 | 나머지 4개 정상 발급 후 `SoldOut` |
| 기간 외 / 강제 중단 / 미워밍업 | 각각 `OutOfPeriod` / `Suspended` / `SystemError` |
| 비동기 적재 (100요청) | API 응답 **19ms** → DB 적재 완료 **1568ms**, 이력 100건 · 중복 0 |
| 쿠폰 소유자 ↔ 이력 일치 | 20건 전부 일치 |

마지막 줄이 이 구조의 요점이다. **API 는 19ms 에 끝나고 DB 는 1.5초에 걸쳐 따라온다.**
유저가 기다리는 시간과 DB 가 감당하는 시간이 분리돼 있다.

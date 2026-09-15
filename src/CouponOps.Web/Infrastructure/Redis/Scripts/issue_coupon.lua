--[[
  선착순 쿠폰 발급 — 재고 차감 · 유저별 한도 · 멱등 처리를 하나의 원자 단위로 수행한다.

  KEYS[1] {evt:<id>}:meta        HASH   startsAtMs, endsAtMs, suspended, perUserLimit, mode
  KEYS[2] {evt:<id>}:pool        LIST   사전 생성 코드 풀        (mode = 0 PreGenerated)
  KEYS[3] {evt:<id>}:stock       STRING 잔여 수량 카운터        (mode = 1 Derived)
  KEYS[4] {evt:<id>}:seq         STRING 코드 파생용 시퀀스      (mode = 1 Derived)
  KEYS[5] {evt:<id>}:users       HASH   userId -> 발급 횟수
  KEYS[6] {evt:<id>}:req:<rid>   STRING 멱등 키 ("<result>|<code>")
  KEYS[7] {evt:<id>}:issued      STREAM DB 적재용 아웃박스

  ARGV[1] userId        ARGV[2] requestId     ARGV[3] nowMs      ARGV[4] reqTtlSeconds
  ARGV[5] logFailure    ARGV[6] codeSecret    ARGV[7] eventId

  반환: { result, couponCode, remaining, priorResult }
        result       IssueResult 값
        remaining    성공 시 잔여 수량, 알 수 없으면 -1
        priorResult  DuplicateRequest 일 때 최초 시도의 결과, 그 외 -1

  ── 왜 이 순서인가 ────────────────────────────────────────────────────────────
  이 스크립트의 순서는 두 가지 원칙으로 결정된다.

  (원칙 1) 멱등 검사가 가장 먼저다.
      재시도가 재고를 두 번 깎는 것을 막아야 하므로, 다른 어떤 검사보다 앞이어야 한다.
      기간·중단 검사를 먼저 하면 "이미 발급받은 유저가 종료 후 재시도" 가 OutOfPeriod 로
      응답되어, 최초 발급 결과와 다른 답이 나간다. 멱등성이 깨진다.

  (원칙 2) 읽기 검증을 전부 끝낸 뒤에야 쓰기를 시작한다.
      Redis 는 Lua 실행 중 오류가 나도 앞서 실행된 명령을 되돌리지 않는다. 트랜잭션 롤백이 없다.
      따라서 "검증 → 쓰기" 로 단계를 가르지 않고 중간에 쓰기를 섞으면,
      뒤쪽 검증에서 거부될 때 앞에서 깎은 재고가 그대로 사라진다.
      아래 4단계 검증은 전부 읽기이고, 쓰기는 5단계부터 시작한다.

      단 하나의 예외가 LPOP 이다. LPOP 은 "재고 확인" 과 "코드 배정" 을 겸하므로 쓰기지만,
      실패 시(nil) 아무것도 변경하지 않으므로 되돌릴 것이 없다. 그래서 첫 번째 쓰기로 둔다.
]]

local userId     = ARGV[1]
local requestId  = ARGV[2]
local nowMs      = tonumber(ARGV[3])
local reqTtl     = tonumber(ARGV[4])
local logFailure = ARGV[5] == '1'
local codeSecret = ARGV[6]
local eventId    = ARGV[7]

local R_SUCCESS, R_SOLDOUT, R_OUTOFPERIOD = 1, 2, 3
local R_LIMIT, R_DUPLICATE, R_SUSPENDED, R_SYSTEM = 4, 5, 6, 99

-- 실패 경로. 재고·한도를 건드리기 전에만 호출되므로 되돌릴 상태가 없다.
-- 실패 이력 적재 여부는 앱이 결정해 넘긴다(logFailure). 소진 후에는 실패가 성공의 수십 배가
-- 되므로, 부하 상황에서 스트림이 실패 로그로 폭주하지 않도록 샘플링 여지를 둔다.
local function fail(result)
  if logFailure then
    redis.call('XADD', KEYS[7], '*',
      'userId', userId, 'requestId', requestId, 'result', result,
      'code', '', 'requestedAtMs', ARGV[3])
  end
  return { result, '', -1, -1 }
end

-- ── 1. 멱등 검사 (원칙 1) ───────────────────────────────────────────────────
-- 최초 처리 결과를 그대로 재생한다. 성공했던 요청의 재시도는 같은 쿠폰 코드를 돌려받는다.
-- 실패했던 요청은 멱등 키를 남기지 않으므로(아래 5단계 참조) 여기서 걸리지 않고 재평가된다.
local prior = redis.call('GET', KEYS[6])
if prior then
  local sep = string.find(prior, '|', 1, true)
  local priorResult = tonumber(string.sub(prior, 1, sep - 1))
  local priorCode = string.sub(prior, sep + 1)
  return { R_DUPLICATE, priorCode, -1, priorResult }
end

-- ── 2. 이벤트 메타 로드 ─────────────────────────────────────────────────────
-- 기간·중단 여부를 앱이 판단한 뒤 스크립트를 호출하면, 판단과 차감 사이에 운영자가
-- 이벤트를 중단할 수 있다(TOCTOU). 그래서 검증을 전부 스크립트 안으로 가져온다.
local meta = redis.call('HMGET', KEYS[1], 'startsAtMs', 'endsAtMs', 'suspended', 'perUserLimit', 'mode')
if not meta[1] then
  -- 메타가 없다 = 워밍업되지 않은 이벤트. 조용히 실패시키면 "재고 0" 과 구분되지 않으므로
  -- SystemError 로 명확히 구분한다.
  return { R_SYSTEM, '', -1, -1 }
end

local startsAtMs   = tonumber(meta[1])
local endsAtMs     = tonumber(meta[2])
local suspended    = meta[3] == '1'
local perUserLimit = tonumber(meta[4])
local mode         = meta[5]

-- ── 3. 거부 검증 (모두 읽기 — 원칙 2) ───────────────────────────────────────
-- 거부 범위가 넓고 비용이 싼 것부터 둔다. 중단된 이벤트는 기간과 무관하게 즉시 거부한다.
if suspended then return fail(R_SUSPENDED) end
if nowMs < startsAtMs then return fail(R_OUTOFPERIOD) end
if nowMs >= endsAtMs then return fail(R_OUTOFPERIOD) end

-- 유저별 한도를 재고보다 먼저 본다. 한도 초과 유저가 재고를 건드리지 못하게 하기 위함이며,
-- HGET 한 번이라 소진 판정보다 싸다.
local issuedCount = tonumber(redis.call('HGET', KEYS[5], userId) or '0')
if issuedCount >= perUserLimit then return fail(R_LIMIT) end

-- ── 4. 재고 확인 + 코드 확보 ────────────────────────────────────────────────
local code
if mode == '0' then
  -- PreGenerated: LPOP 이 재고 판정과 코드 배정을 겸한다.
  -- "재고는 깎였는데 코드가 없다" 는 상태가 구조적으로 생길 수 없다 — 두 자원이 하나다.
  code = redis.call('LPOP', KEYS[2])
  if not code then return fail(R_SOLDOUT) end   -- nil = 풀이 비었다 = 소진. 변경된 것 없음.
else
  -- Derived: 재고 카운터와 코드가 별개 자원이므로 원자성을 여기서 보증해야 한다.
  -- 스크립트 전체가 원자적이라 "확인 후 차감" 이 안전하다. 이것이 Lua 를 쓰는 이유다.
  local stock = tonumber(redis.call('GET', KEYS[3]) or '0')
  if stock <= 0 then return fail(R_SOLDOUT) end
  redis.call('DECR', KEYS[3])

  -- 코드는 단조 시퀀스에서 파생한다.
  --   앞부분(시퀀스)  — 유일성을 구조적으로 보증한다. 해시 충돌에 기대지 않는다.
  --   뒷부분(해시)    — 추측 불가능성. 시퀀스만 노출하면 남의 코드를 열거할 수 있다.
  local seq = redis.call('INCR', KEYS[4])
  local suffix = string.upper(string.sub(redis.sha1hex(codeSecret .. ':' .. eventId .. ':' .. seq), 1, 12))
  code = string.format('E%s-%08X-%s', eventId, seq, suffix)
end

-- ── 5. 발급 확정 (여기서부터 쓰기 — 원칙 2) ─────────────────────────────────
-- 모든 검증을 통과한 뒤이므로, 아래 쓰기들은 도중에 거부될 여지가 없다.
redis.call('HINCRBY', KEYS[5], userId, 1)

-- 멱등 키는 성공에만 남긴다. 실패에 남기면 운영자가 수량을 늘린 뒤의 재시도까지
-- DuplicateRequest 로 막히고, "중복 요청" 이라는 실패 사유의 의미도 흐려진다.
redis.call('SET', KEYS[6], R_SUCCESS .. '|' .. code, 'EX', reqTtl)

-- DB 적재는 이 스트림을 통해 비동기로 이뤄진다. Redis 가 source of truth 이므로
-- 여기까지 성공하면 발급은 확정이고, DB 반영 실패는 재처리 대상이지 발급 취소 사유가 아니다.
redis.call('XADD', KEYS[7], '*',
  'userId', userId, 'requestId', requestId, 'result', R_SUCCESS,
  'code', code, 'requestedAtMs', ARGV[3])

local remaining
if mode == '0' then
  remaining = redis.call('LLEN', KEYS[2])
else
  remaining = tonumber(redis.call('GET', KEYS[3]) or '0')
end

return { R_SUCCESS, code, remaining, -1 }

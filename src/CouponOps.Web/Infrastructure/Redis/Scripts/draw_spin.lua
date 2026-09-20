--[[
  룰렛 추첨 — 슬롯 결정 · 재고 차감 · 티켓 차감 · 천장 · 일일 한도 · 멱등 처리를
  하나의 원자 단위로 수행한다.

  KEYS[1] {drw:<id>}:meta           HASH   startsAtMs, endsAtMs, suspended, dailyLimit,
                                           ticketCost, pityThreshold, pityPrizeId,
                                           weightVersionId, totalWeight
  KEYS[2] {drw:<id>}:cum            LIST   누적 가중치 "prizeId|cumWeight|fallbackId|isBlank|itemId|qty"
  KEYS[3] {drw:<id>}:stock          HASH   prizeId -> 잔여 재고 (유한 재고 슬롯만 존재. 없으면 무제한)
  KEYS[4] {drw:<id>}:daily:<ymd>    HASH   userId -> 그날 추첨 횟수
  KEYS[5] {drw:<id>}:pity           HASH   userId -> 연속 꽝 횟수
  KEYS[6] {drw:<id>}:tickets        HASH   userId -> 잔여 티켓
  KEYS[7] {drw:<id>}:req:<rid>      STRING 멱등 키
  KEYS[8] {drw:<id>}:drawn          STREAM DB 적재용 아웃박스
  KEYS[9] {drw:<id>}:won            HASH   prizeId -> 당첨 누적 수 (편차 모니터링)

  ARGV[1] userId       ARGV[2] requestId   ARGV[3] nowMs      ARGV[4] randomValue
  ARGV[5] reqTtlSec    ARGV[6] logFailure  ARGV[7] dailyTtlSec

  반환: { result, prizeId, itemId, qty, roll, fallbackApplied, pityApplied, pityCountAfter,
          remainingTickets, remainingDraws, priorResult, weightVersionId, originalPrizeId, totalWeight }

  ── 왜 이 순서인가 ──────────────────────────────────────────────────────────
  쿠폰 발급 스크립트(issue_coupon.lua)의 두 원칙을 그대로 계승한다.

  (원칙 1) 멱등 검사가 가장 먼저다.
      재시도가 티켓을 두 번 깎거나 재추첨이 되는 것을 막아야 하므로 다른 어떤 검사보다 앞이다.
      네트워크 타임아웃마다 유저가 다시 돌릴 수 있게 되면, 그건 확률 이벤트가 아니다.

  (원칙 2) 읽기 검증을 전부 끝낸 뒤에야 쓰기를 시작한다.
      Redis 는 Lua 실행 중 오류가 나도 앞서 실행된 명령을 되돌리지 않는다. 롤백이 없다.

  여기에 룰렛 고유의 원칙이 하나 붙는다.

  (원칙 3) 티켓 차감은 쓰기 구간의 맨 앞이고, 그 뒤로 실패 분기가 없어야 한다.
      "티켓은 빠졌는데 아무것도 못 받았다" 가 유저 입장에서 가장 나쁜 실패다.
      그래서 슬롯 결정과 재고 판정·대체 치환까지 전부 읽기 단계(5~6)에서 끝낸다.
      7단계에 들어선 뒤에는 거부될 여지가 없다.

  ── 난수를 여기서 뽑지 않는 이유 ────────────────────────────────────────────
  randomValue 는 앱(.NET RandomNumberGenerator)이 뽑아 ARGV 로 넘긴다.
  원자성이 필요한 것은 "선택 + 차감" 이지 "난수 생성" 이 아니고,
  밖에서 만들어야 그 값을 이력에 남겨 사후에 추첨을 그대로 재계산할 수 있다.
  Lua 의 math.random 을 쓰면 그 순간 이 시스템은 검증 불가능해진다.
]]

local userId     = ARGV[1]
local requestId  = ARGV[2]
local nowMs      = tonumber(ARGV[3])
local randomVal  = tonumber(ARGV[4])
local reqTtl     = tonumber(ARGV[5])
local logFailure = ARGV[6] == '1'
local dailyTtl   = tonumber(ARGV[7])

local R_WON, R_OUTOFPERIOD, R_DAILY = 1, 3, 4
local R_DUPLICATE, R_SUSPENDED, R_TICKET, R_SOLDOUT, R_SYSTEM = 5, 6, 7, 8, 99

local function split(s)
  local t = {}
  for part in string.gmatch(s, '([^|]+)') do t[#t + 1] = part end
  return t
end

-- 실패 경로. 재고·티켓·천장을 건드리기 전에만 호출되므로 되돌릴 상태가 없다.
-- 실패 이력 적재 여부는 앱이 정해 넘긴다(logFailure) — 소진·한도 초과가 폭주할 때
-- 스트림이 실패 로그로 가득 차지 않도록 샘플링 여지를 둔다.
local function fail(result)
  if logFailure then
    redis.call('XADD', KEYS[8], '*',
      'userId', userId, 'requestId', requestId, 'result', result,
      'prizeId', 0, 'originalPrizeId', 0, 'itemId', 0, 'qty', 0,
      'ticketsSpent', 0, 'fallback', 0, 'pity', 0, 'pityAfter', -1,
      'weightVersionId', 0, 'randomValue', 0, 'roll', -1, 'totalWeight', 0,
      'requestedAtMs', ARGV[3])
  end
  return { result, 0, 0, 0, -1, 0, 0, -1, -1, -1, -1, 0, 0, 0 }
end

-- ── 1. 멱등 검사 (원칙 1) ───────────────────────────────────────────────────
-- 최초 결과를 그대로 재생한다. 같은 requestId 로 몇 번을 다시 보내도 같은 경품이 나온다.
-- 실패는 멱등 키를 남기지 않으므로(7단계 참조) 여기서 걸리지 않고 재평가된다 —
-- 운영자가 재고를 늘린 뒤의 재시도까지 "중복 요청" 으로 막히면 안 되기 때문이다.
local prior = redis.call('GET', KEYS[7])
if prior then
  local p = split(prior)
  -- 잔여 티켓은 "지금" 값을 돌려준다(최초 시도 시점의 값이 아니다).
  -- 그 사이 다른 추첨으로 티켓이 더 줄었을 수 있고, 클라이언트가 알아야 하는 것은 현재 잔액이다.
  -- 남은 일일 횟수는 -1 로 둔다 — 재생은 횟수를 소모하지 않으므로 알릴 값이 없다.
  local t = redis.call('HGET', KEYS[6], userId)
  local tickets = t and tonumber(t) or -1
  return { R_DUPLICATE, tonumber(p[2]), tonumber(p[3]), tonumber(p[4]), tonumber(p[5]),
           tonumber(p[6]), tonumber(p[7]), tonumber(p[8]), tickets, -1,
           tonumber(p[1]), tonumber(p[9]), tonumber(p[10]), tonumber(p[11]) }
end

-- ── 2. 메타 로드 ────────────────────────────────────────────────────────────
-- 기간·중단 판단을 앱에서 하면 판단과 차감 사이에 운영자가 이벤트를 중단할 수 있다(TOCTOU).
-- 그래서 검증을 전부 스크립트 안으로 가져온다.
local meta = redis.call('HMGET', KEYS[1],
  'startsAtMs', 'endsAtMs', 'suspended', 'dailyLimit', 'ticketCost',
  'pityThreshold', 'pityPrizeId', 'weightVersionId', 'totalWeight')

if not meta[1] then
  -- 메타가 없다 = 워밍업되지 않은 이벤트. 조용히 실패시키면 "전부 소진" 과 구분되지 않는다.
  return { R_SYSTEM, 0, 0, 0, -1, 0, 0, -1, -1, -1, -1, 0, 0, 0 }
end

local startsAtMs   = tonumber(meta[1])
local endsAtMs     = tonumber(meta[2])
local suspended    = meta[3] == '1'
local dailyLimit   = tonumber(meta[4])
local ticketCost   = tonumber(meta[5])
local pityThresh   = tonumber(meta[6])
local pityPrizeId  = tonumber(meta[7])
local weightVerId  = tonumber(meta[8])
local totalWeight  = tonumber(meta[9])

if totalWeight == nil or totalWeight <= 0 then
  return { R_SYSTEM, 0, 0, 0, -1, 0, 0, -1, -1, -1, -1, 0, 0, 0 }
end

-- ── 3. 거부 검증 (전부 읽기 — 원칙 2) ───────────────────────────────────────
-- 거부 범위가 넓고 싼 것부터 둔다. 중단은 기간과 무관하게 즉시 거부한다.
if suspended then return fail(R_SUSPENDED) end
if nowMs < startsAtMs then return fail(R_OUTOFPERIOD) end
if nowMs >= endsAtMs then return fail(R_OUTOFPERIOD) end

local usedToday = tonumber(redis.call('HGET', KEYS[4], userId) or '0')
if dailyLimit > 0 and usedToday >= dailyLimit then return fail(R_DAILY) end

-- 티켓 확인을 마지막에 둔다. 다른 사유로 거부될 요청이 티켓 조회까지 가지 않게 한다.
local ticketsHeld = -1
if ticketCost > 0 then
  ticketsHeld = tonumber(redis.call('HGET', KEYS[6], userId) or '0')
  if ticketsHeld < ticketCost then return fail(R_TICKET) end
end

-- ── 4~5. 슬롯 결정 (읽기, 순수 계산) ────────────────────────────────────────
-- 누적 가중치 배열은 워밍업 때 만들어 두고 이벤트 기간 내내 불변이다.
-- 대체(Fallback) 정책을 택한 덕분이다 — 재분배 정책이었다면 소진 상태에 따라
-- 매 추첨마다 이 배열을 다시 만들어야 하고, 그 사이의 경합까지 다뤄야 한다.
local entries = redis.call('LRANGE', KEYS[2], 0, -1)
if #entries == 0 then return fail(R_SYSTEM) end

local function findByPrize(pid)
  for i = 1, #entries do
    local e = split(entries[i])
    if tonumber(e[1]) == pid then return e end
  end
  return nil
end

local chosen, roll, pityApplied

-- 천장: 난수를 쓰지 않고 지급 슬롯을 확정한다.
-- 이력에 pityApplied 로 남겨야 재현 시 같은 분기를 탈 수 있다(roll 은 -1).
--
-- 경계에 주의한다. "천장 10회" 는 실무에서 <10회 안에 반드시 당첨된다> 는 뜻이므로,
-- 확정이 걸리는 것은 꽝이 10번 쌓인 다음(11번째)이 아니라 10번째 추첨 자체다.
-- 그래서 비교 대상은 pityCount 가 아니라 "이번 추첨까지 포함한 횟수" 인 pityCount + 1 이다.
local pityCount = 0
if pityThresh > 0 then
  pityCount = tonumber(redis.call('HGET', KEYS[5], userId) or '0')
end

if pityThresh > 0 and pityCount + 1 >= pityThresh and pityPrizeId > 0 then
  chosen = findByPrize(pityPrizeId)
  if not chosen then return fail(R_SYSTEM) end
  roll = -1
  pityApplied = 1
else
  roll = randomVal % totalWeight
  pityApplied = 0
  for i = 1, #entries do
    local e = split(entries[i])
    if roll < tonumber(e[2]) then
      chosen = e
      break
    end
  end
  -- 누적 합이 totalWeight 에 못 미친다 = 워밍업 데이터 손상. 조용히 마지막 슬롯을
  -- 주면 확률이 왜곡된 채로 이벤트가 계속 돈다. 명시적으로 실패시킨다.
  if not chosen then return fail(R_SYSTEM) end
end

-- ── 6. 재고 판정 · 대체 치환 (읽기) ─────────────────────────────────────────
-- 여기까지 아무것도 쓰지 않았다. 지금 거부해도 잃는 것이 없다 — 원칙 3 의 전제다.
local originalPrizeId = tonumber(chosen[1])
local fallbackApplied = 0
local chosenFinite = false
local hops = 0

while true do
  local pid = tonumber(chosen[1])
  local stock = redis.call('HGET', KEYS[3], pid)

  if not stock then chosenFinite = false break end          -- 무제한 재고
  if tonumber(stock) > 0 then chosenFinite = true break end  -- 재고 있음

  -- 소진. 대체 경품으로 치환한다. 대체 대상은 도메인 검증에서 "무제한 재고" 로
  -- 못박혀 있으므로 정상 데이터라면 한 번에 끝난다. hops 는 데이터가 손상됐을 때의 방어다.
  local fid = tonumber(chosen[3])
  if fid == 0 or hops >= 2 then return fail(R_SOLDOUT) end

  local nxt = findByPrize(fid)
  if not nxt then return fail(R_SYSTEM) end

  chosen = nxt
  fallbackApplied = 1
  hops = hops + 1
end

local prizeId = tonumber(chosen[1])
local isBlank = tonumber(chosen[4]) == 1
local itemId  = tonumber(chosen[5])
local qty     = tonumber(chosen[6])

-- ── 7. 확정 (여기서부터 쓰기 — 원칙 2·3) ────────────────────────────────────
-- 모든 판정이 끝났다. 아래 쓰기들은 도중에 거부될 여지가 없다.

-- 7-1. 티켓 차감을 가장 먼저 한다. 이 시점 이후로 실패가 없으므로
--      "티켓만 빠지는" 상태가 구조적으로 생기지 않는다.
local remainingTickets = -1
if ticketCost > 0 then
  remainingTickets = redis.call('HINCRBY', KEYS[6], userId, -ticketCost)
end

-- 7-2. 재고 차감. 무제한 슬롯은 해시에 아예 없으므로 건드리지 않는다.
if chosenFinite then
  redis.call('HINCRBY', KEYS[3], prizeId, -1)
end

-- 7-3. 천장 갱신. 꽝이면 누적, 당첨이면 리셋.
local pityAfter = -1
if pityThresh > 0 then
  if isBlank then
    pityAfter = redis.call('HINCRBY', KEYS[5], userId, 1)
  else
    redis.call('HSET', KEYS[5], userId, 0)
    pityAfter = 0
  end
end

-- 7-4. 일일 카운터. 리셋 시각은 앱이 키 이름(:daily:<ymd>)으로 정한다 —
--      타임존·리셋 시각 계산을 스크립트 안에 넣으면 서머타임까지 Lua 로 다뤄야 한다.
local usedAfter = redis.call('HINCRBY', KEYS[4], userId, 1)
redis.call('EXPIRE', KEYS[4], dailyTtl)
local remainingDraws = -1
if dailyLimit > 0 then remainingDraws = dailyLimit - usedAfter end

-- 7-5. 경품별 당첨 누적. 운영툴의 기대값 대비 편차(카이제곱) 모니터링이 이 값을 읽는다.
redis.call('HINCRBY', KEYS[9], prizeId, 1)

-- 7-6. 멱등 키. 성공에만 남긴다.
redis.call('SET', KEYS[7],
  R_WON .. '|' .. prizeId .. '|' .. itemId .. '|' .. qty .. '|' .. roll .. '|' ..
  fallbackApplied .. '|' .. pityApplied .. '|' .. pityAfter .. '|' ..
  weightVerId .. '|' .. originalPrizeId .. '|' .. totalWeight,
  'EX', reqTtl)

-- 7-7. 아웃박스. DB 적재(이력 + 우편함)는 이 스트림을 통해 비동기로 이뤄진다.
--      여기까지 성공하면 추첨은 확정이고, DB 반영 실패는 재처리 대상이지 추첨 취소 사유가 아니다.
redis.call('XADD', KEYS[8], '*',
  'userId', userId, 'requestId', requestId, 'result', R_WON,
  'prizeId', prizeId, 'originalPrizeId', originalPrizeId,
  'itemId', itemId, 'qty', qty,
  'ticketsSpent', ticketCost, 'fallback', fallbackApplied,
  'pity', pityApplied, 'pityAfter', pityAfter,
  'weightVersionId', weightVerId, 'randomValue', ARGV[4],
  'roll', roll, 'totalWeight', totalWeight,
  'requestedAtMs', ARGV[3])

return { R_WON, prizeId, itemId, qty, roll, fallbackApplied, pityApplied, pityAfter,
         remainingTickets, remainingDraws, -1, weightVerId, originalPrizeId, totalWeight }

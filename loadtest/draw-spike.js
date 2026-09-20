import http from 'k6/http';
import { check } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';

/**
 * 룰렛 오픈 직후의 트래픽 스파이크를 재현한다.
 *
 * 쿠폰 발급과 증명하려는 성질이 다르다.
 *   쿠폰  — "정확히 N개" (단일 카운터)
 *   룰렛  — "한정 경품이 재고를 넘겨 나가지 않는다" (경품별 카운터) + "분포가 가중치와 같다"
 * 그래서 결과별 카운터에 더해 슬롯별 당첨 수를 따로 센다. 소진 이후의 대체 치환까지
 * 세어야 "재고 초과 0건" 을 사후에 검증할 수 있다.
 *
 * 환경변수
 *   BASE_URL   대상 주소               (기본 http://127.0.0.1:5080)
 *   DRAW_ID    대상 룰렛 ID            (필수)
 *   VUS        스파이크 정점 VU 수     (기본 1000)
 *   HOLD       정점 유지 시간          (기본 20s)
 *   DUP_RATE   동일 requestId 재시도 비율 (기본 0.2)
 */
const BASE_URL = __ENV.BASE_URL || 'http://127.0.0.1:5080';
const DRAW_ID  = __ENV.DRAW_ID;
const VUS      = parseInt(__ENV.VUS || '1000', 10);
const HOLD     = __ENV.HOLD || '20s';
const DUP_RATE = parseFloat(__ENV.DUP_RATE || '0.2');

const rWon       = new Counter('result_won');
const rTicket    = new Counter('result_insufficient_ticket');
const rDaily     = new Counter('result_daily_limit');
const rDuplicate = new Counter('result_duplicate');
const rPeriod    = new Counter('result_out_of_period');
const rSuspended = new Counter('result_suspended');
const rSoldOut   = new Counter('result_all_sold_out');
const rSystem    = new Counter('result_system_error');
const rOther     = new Counter('result_other');

// 슬롯별 당첨 수. 0번이 한정 경품이고, 재고를 넘지 않았는지가 이 측정의 핵심이다.
const slot0 = new Counter('slot0_wins');
const slot1 = new Counter('slot1_wins');
const slot2 = new Counter('slot2_wins');
const slotOther = new Counter('slot_other_wins');

// 재고 소진 이후 대체 경품으로 치환된 횟수. slot0 당첨 + 치환 = slot0 을 뽑은 총횟수여야 한다.
const fallbacks = new Counter('fallback_applied');
const pities = new Counter('pity_applied');

const serverLatency = new Trend('server_latency_ms', true);
const okRate = new Rate('handled_ok');

export const options = {
  discardResponseBodies: false,
  scenarios: {
    spike: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '10s', target: VUS },
        { duration: HOLD,  target: VUS },
        { duration: '5s',  target: 0 },
      ],
      gracefulRampDown: '10s',
    },
  },
  // 409(한도·티켓·소진)는 정상 판정이므로 실패가 아니다.
  // 서버가 죽거나 프로토콜 오류가 나는 것만 실패로 본다.
  thresholds: {
    handled_ok: ['rate>0.99'],
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(95)', 'p(99)', 'max'],
};

/**
 * VU 별로 마지막에 쓴 requestId 를 들고 있는다.
 * 재시도는 "새 난수로 한 번 더" 가 아니라 "같은 요청을 다시 보내는 것" 이어야
 * 멱등 경로(DuplicateRequest)를 실제로 밟는다.
 */
let lastRequestId = null;

function uuid() {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    return (c === 'x' ? r : (r & 0x3) | 0x8).toString(16);
  });
}

export default function () {
  const retry = lastRequestId !== null && Math.random() < DUP_RATE;
  const requestId = retry ? lastRequestId : uuid();
  const userId = `vu${__VU}-iter${retry ? __ITER - 1 : __ITER}`;

  if (!retry) lastRequestId = requestId;

  const res = http.post(
    `${BASE_URL}/api/draws/${DRAW_ID}/spin`,
    JSON.stringify({ userId, requestId }),
    { headers: { 'Content-Type': 'application/json' }, tags: { path: 'spin' } },
  );

  let body = null;
  try { body = res.json(); } catch (_) { /* 비 JSON 응답 */ }

  okRate.add(res.status === 200 || res.status === 409 || res.status === 503);
  if (body && body.latencyMs !== undefined) serverLatency.add(body.latencyMs);

  switch (body && body.result) {
    case 'Won':                rWon.add(1); break;
    case 'InsufficientTicket': rTicket.add(1); break;
    case 'DailyLimitExceeded': rDaily.add(1); break;
    case 'DuplicateRequest':   rDuplicate.add(1); break;
    case 'OutOfPeriod':        rPeriod.add(1); break;
    case 'Suspended':          rSuspended.add(1); break;
    case 'AllPrizesSoldOut':   rSoldOut.add(1); break;
    case 'SystemError':        rSystem.add(1); break;
    default:                   rOther.add(1); break;
  }

  // 멱등 재생(DuplicateRequest)은 새 당첨이 아니므로 슬롯 카운터에서 제외한다 —
  // 포함시키면 "재고보다 많이 당첨됐다" 는 거짓 결론이 나온다.
  if (body && body.result === 'Won' && body.prize) {
    switch (body.prize.slotIndex) {
      case 0: slot0.add(1); break;
      case 1: slot1.add(1); break;
      case 2: slot2.add(1); break;
      default: slotOther.add(1); break;
    }
    if (body.fallbackApplied) fallbacks.add(1);
    if (body.pityApplied) pities.add(1);
  }

  check(res, { '서버가 판정을 내림': (r) => r.status === 200 || r.status === 409 });
}

export function handleSummary(data) {
  const out = {};
  if (__ENV.SUMMARY_OUT) out[__ENV.SUMMARY_OUT] = JSON.stringify(data, null, 2);
  out.stdout = textSummary(data);
  return out;
}

function textSummary(data) {
  const m = data.metrics;
  const n = (metric, key, d = 0) => {
    const v = ((m[metric] || {}).values || {})[key];
    return v === undefined ? '-' : Number(v).toFixed(d);
  };
  return [
    `  룰렛 ${DRAW_ID}  /  VU ${VUS}`,
    `  처리량          ${n('http_reqs', 'rate', 1)} req/s`,
    `  응답시간 p50    ${n('http_req_duration', 'med', 1)} ms`,
    `  응답시간 p95    ${n('http_req_duration', 'p(95)', 1)} ms`,
    `  응답시간 p99    ${n('http_req_duration', 'p(99)', 1)} ms`,
    `  서버 처리 p99   ${n('server_latency_ms', 'p(99)', 1)} ms`,
    '',
    `  당첨            ${n('result_won', 'count')}`,
    `  멱등 재생       ${n('result_duplicate', 'count')}`,
    `  일일 한도       ${n('result_daily_limit', 'count')}`,
    `  시스템 오류     ${n('result_system_error', 'count')}`,
    '',
    `  슬롯0 (한정)    ${n('slot0_wins', 'count')}   ← 재고를 넘으면 안 된다`,
    `  슬롯1           ${n('slot1_wins', 'count')}`,
    `  슬롯2 (대체)    ${n('slot2_wins', 'count')}`,
    `  대체 치환       ${n('fallback_applied', 'count')}`,
    `  천장 적용       ${n('pity_applied', 'count')}`,
    '',
  ].join('\n');
}

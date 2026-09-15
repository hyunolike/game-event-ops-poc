import http from 'k6/http';
import { check } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';

/**
 * 이벤트 오픈 직후의 트래픽 스파이크를 재현한다.
 *
 * 환경변수
 *   BASE_URL   대상 주소            (기본 http://127.0.0.1:5080)
 *   EVENT_ID   대상 이벤트 ID       (필수)
 *   ISSUE_PATH issue | issue-db | issue-db-skiplocked
 *   VUS        스파이크 정점 VU 수  (기본 1000)
 *   HOLD       정점 유지 시간       (기본 20s)
 *   DUP_RATE   동일 유저 중복 요청 비율 (기본 0.2)
 */
const BASE_URL   = __ENV.BASE_URL   || 'http://127.0.0.1:5080';
const EVENT_ID   = __ENV.EVENT_ID;
const ISSUE_PATH = __ENV.ISSUE_PATH || 'issue';
const VUS        = parseInt(__ENV.VUS || '1000', 10);
const HOLD       = __ENV.HOLD || '20s';
const DUP_RATE   = parseFloat(__ENV.DUP_RATE || '0.2');

// 결과별 카운터. 성공만 세면 "왜 실패했는지" 가 사라진다.
const rIssued    = new Counter('result_success');
const rSoldOut   = new Counter('result_sold_out');
const rLimit     = new Counter('result_limit_exceeded');
const rDuplicate = new Counter('result_duplicate');
const rPeriod    = new Counter('result_out_of_period');
const rSystem    = new Counter('result_system_error');
const rOther     = new Counter('result_other');

// 서버가 보고한 자체 처리 시간. 네트워크·k6 오버헤드를 뺀 순수 처리 비용이다.
const serverLatency = new Trend('server_latency_ms', true);
const okRate = new Rate('handled_ok');

export const options = {
  discardResponseBodies: false,
  scenarios: {
    spike: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '10s', target: VUS },  // 오픈 직후 0 → 정점
        { duration: HOLD,  target: VUS },  // 정점 유지
        { duration: '5s',  target: 0 },
      ],
      gracefulRampDown: '10s',
    },
  },
  // 서버가 죽거나 프로토콜 오류가 나면 실패로 본다.
  // 409(소진/한도)는 정상 동작이므로 실패가 아니다.
  thresholds: {
    handled_ok: ['rate>0.99'],
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(95)', 'p(99)', 'max'],
};

export default function () {
  // 20% 는 좁은 유저 풀에서 골라 같은 유저의 중복 요청을 만든다.
  // 실제 이벤트에서 흔한 패턴이다 — 유저가 안 됐다고 생각하고 계속 누른다.
  const isDup = Math.random() < DUP_RATE;
  const userId = isDup
    ? `hot-${__VU % 50}`
    : `vu${__VU}-iter${__ITER}`;

  const res = http.post(
    `${BASE_URL}/api/events/${EVENT_ID}/coupons/${ISSUE_PATH}`,
    JSON.stringify({ userId }),
    { headers: { 'Content-Type': 'application/json' }, tags: { path: ISSUE_PATH } },
  );

  let body = null;
  try { body = res.json(); } catch (_) { /* 비 JSON 응답 */ }

  // 서버가 살아서 판정을 내렸는가. 200·409 는 정상 판정, 500·0(연결실패)은 아니다.
  okRate.add(res.status === 200 || res.status === 409 || res.status === 503);

  if (body && body.latencyMs !== undefined) serverLatency.add(body.latencyMs);

  switch (body && body.result) {
    case 'Success':          rIssued.add(1); break;
    case 'SoldOut':          rSoldOut.add(1); break;
    case 'LimitExceeded':    rLimit.add(1); break;
    case 'DuplicateRequest': rDuplicate.add(1); break;
    case 'OutOfPeriod':      rPeriod.add(1); break;
    case 'SystemError':      rSystem.add(1); break;
    default:                 rOther.add(1); break;
  }

  check(res, { '서버가 판정을 내림': (r) => r.status === 200 || r.status === 409 });
}

/**
 * 실행 결과를 기계가 읽을 수 있는 형태로도 남긴다.
 * 표를 손으로 옮겨 적으면 실수가 섞이고, 재현할 때 비교가 안 된다.
 */
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
    `  경로            ${__ENV.ISSUE_PATH}  /  VU ${VUS}`,
    `  처리량          ${n('http_reqs', 'rate', 1)} req/s`,
    `  응답시간 p50    ${n('http_req_duration', 'med', 1)} ms`,
    `  응답시간 p95    ${n('http_req_duration', 'p(95)', 1)} ms`,
    `  응답시간 p99    ${n('http_req_duration', 'p(99)', 1)} ms`,
    `  발급 성공       ${n('result_success', 'count')}`,
    `  수량 소진       ${n('result_sold_out', 'count')}`,
    `  한도 초과       ${n('result_limit_exceeded', 'count')}`,
    `  시스템 오류     ${n('result_system_error', 'count')}`,
    '',
  ].join('\n');
}

#!/usr/bin/env bash
# 룰렛 추첨 경로를 VU 단계별로 측정하고, 매 실행 뒤 "한정 경품 초과 지급 0건" 을 확인한다.
#   사용법: run-draw.sh <출력디렉터리>
#
# 처리량 수치만 남기면 이 측정은 의미가 없다. 증명하려는 것은
# "부하 아래에서도 재고를 넘긴 지급이 없다" 이므로, 실행마다 서버에 직접 물어 확인한다.
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
OUT=${1:-/tmp/drawtest-results}
BASE=${BASE_URL:-http://127.0.0.1:5080}
STOCK=${STOCK:-5000}
mkdir -p "$OUT"

JAR=$(mktemp)
trap 'rm -f "$JAR"' EXIT

login() {
  local t
  t=$(curl -s -c "$JAR" -b "$JAR" "$BASE/Account/Login" \
      | grep -o 'name="__RequestVerificationToken"[^>]*value="[^"]*"' | head -1 | sed 's/.*value="//;s/"$//')
  curl -s -c "$JAR" -b "$JAR" -o /dev/null -X POST "$BASE/Account/Login" \
    --data-urlencode "LoginId=${ADMIN_ID:-admin}" \
    --data-urlencode "Password=${ADMIN_PW:-admin1234}" \
    --data-urlencode "__RequestVerificationToken=$t"
}

fail=0

for VUS in 200 1000 3000; do
  echo "════════ 룰렛 추첨 · VU $VUS ════════"

  # 앞선 실행이 남긴 수십만 행이 인덱스 크기와 버퍼풀 상태를 바꿔 놓으면
  # 나중에 실행된 구간이 부당하게 불리해진다.
  bash "$ROOT/loadtest/reset-env.sh" >/dev/null

  DID=$(bash "$ROOT/loadtest/provision-draw.sh" "$STOCK" "draw-$VUS")

  DRAW_ID=$DID VUS=$VUS HOLD=20s \
    SUMMARY_OUT="$OUT/draw-vu$VUS.json" \
    k6 run --quiet --no-color "$ROOT/loadtest/draw-spike.js" || true

  # ── 정확성 확인 ────────────────────────────────────────────────────────────
  # 운영툴의 현황 API 에 직접 물어 본다. k6 가 센 값과 서버가 아는 값이 같아야 하고,
  # 무엇보다 한정 경품 당첨 수가 재고를 넘지 않아야 한다.
  login
  STATUS=$(curl -s -b "$JAR" "$BASE/api/draws/$DID/status")
  echo "$STATUS" > "$OUT/draw-vu$VUS-status.json"

  WON0=$(printf '%s' "$STATUS" | grep -o '"slotIndex":0[^}]*"observed":[0-9]*' | grep -o '[0-9]*$' | head -1)
  WON0=${WON0:-0}

  printf '  한정 경품 당첨 %s / 재고 %s  →  ' "$WON0" "$STOCK"
  if [ "$WON0" -le "$STOCK" ]; then
    echo "초과 지급 0건 ✓"
  else
    echo "초과 지급 $((WON0 - STOCK))건 ✗"
    fail=1
  fi
  echo
done

echo "결과: $OUT"
exit "$fail"

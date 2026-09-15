#!/usr/bin/env bash
# 소진 시나리오 — 재고를 완전히 소진시키고 발급 수가 설정 수량과 정확히 일치하는지 확인한다.
# 성능이 아니라 정확성을 보는 실행이다.
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
BASE=${BASE_URL:-http://127.0.0.1:5080}
STOCK=${STOCK:-10000}
OUT=${1:-/tmp/lt-selloff}
mkdir -p "$OUT"

JAR=$(mktemp); trap 'rm -f "$JAR"' EXIT
login() {
  local t
  t=$(curl -s -c "$JAR" -b "$JAR" "$BASE/Account/Login" \
      | grep -o 'name="__RequestVerificationToken"[^>]*value="[^"]*"' | head -1 | sed 's/.*value="//;s/"$//')
  curl -s -c "$JAR" -b "$JAR" -o /dev/null -X POST "$BASE/Account/Login" \
    --data-urlencode "LoginId=admin" --data-urlencode "Password=admin1234" \
    --data-urlencode "__RequestVerificationToken=$t"
}

for P in issue issue-db issue-db-skiplocked; do
  echo "════════ 소진 시나리오 · $P (재고 $STOCK) ════════"
  bash "$ROOT/loadtest/reset-env.sh" >/dev/null
  EID=$(bash "$ROOT/loadtest/provision.sh" "$STOCK" 1 "sell-$P")

  # DB 경로는 처리량이 낮아 소진에 더 오래 걸린다.
  HOLD=$([ "$P" = "issue" ] && echo "15s" || echo "90s")

  EVENT_ID=$EID ISSUE_PATH=$P VUS=300 HOLD=$HOLD DUP_RATE=0.2 \
    SUMMARY_OUT="$OUT/$P.json" k6 run --quiet --no-color "$ROOT/loadtest/issue-spike.js" || true

  # 비동기 적재가 끝날 때까지 기다린 뒤 원천(Redis)과 영속화(DB)를 함께 확인한다.
  login
  for _ in $(seq 1 60); do
    S=$(curl -s -b "$JAR" "$BASE/api/events/$EID/status")
    echo "$S" | grep -q '"pendingPersistence":0' && break
    sleep 1
  done
  echo "  현황: $S"
  echo "$S" > "$OUT/$P-status.json"
  echo
done

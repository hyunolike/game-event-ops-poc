#!/usr/bin/env bash
# 세 발급 경로를 같은 조건으로 측정한다.
#   사용법: run-comparison.sh <출력디렉터리>
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
OUT=${1:-/tmp/loadtest-results}
mkdir -p "$OUT"

# 경로별 재고. DB 경로는 처리량이 낮아 큰 재고가 필요 없다.
# 재고가 소진되면 그 뒤로는 값싼 거부 경로만 측정하게 되므로, 각 경로가
# "발급을 계속하는 상태" 로 측정 구간을 채우도록 맞춘다.
declare -A STOCK=( [issue]=400000 [issue-db]=20000 [issue-db-skiplocked]=20000 )

for VUS in 200 1000 3000; do
  for P in issue issue-db issue-db-skiplocked; do
    echo "════════ $P · VU $VUS ════════"

    # 매 실행 전에 DB·Redis 를 비운다. 앞선 실행이 남긴 수십만 행이 인덱스 크기와
    # 버퍼풀 상태를 바꿔 놓으면, 나중에 실행된 경로가 부당하게 불리해진다.
    bash "$ROOT/loadtest/reset-env.sh" >/dev/null

    EID=$(bash "$ROOT/loadtest/provision.sh" "${STOCK[$P]}" 1 "cmp-$P-$VUS")

    EVENT_ID=$EID ISSUE_PATH=$P VUS=$VUS HOLD=20s \
      SUMMARY_OUT="$OUT/$P-vu$VUS.json" \
      k6 run --quiet --no-color "$ROOT/loadtest/issue-spike.js" || true

    echo
  done
done

echo "결과: $OUT"

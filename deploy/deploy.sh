#!/usr/bin/env bash
#
# 헬스체크 기반 무중단 배포 (blue-green).
#
#   deploy.sh <이미지> [--simulate-failure]
#
# 흐름:
#   1. 현재 색깔을 확인하고 반대 색깔로 새 인스턴스를 띄운다
#   2. 스키마를 적용한다
#   3. 새 인스턴스가 /health/ready 로 200 을 줄 때까지 기다린다  ← 트래픽 전환의 게이트
#   4. nginx upstream 을 새 색깔로 바꾸고 reload
#   5. 프록시를 통해 스모크 테스트
#   6. 성공하면 옛 인스턴스를 내린다
#
# 어느 단계에서 실패하든 트래픽은 옛 인스턴스에 남는다. 그것이 롤백 경로다.
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
UPSTREAM_FILE="$ROOT/deploy/nginx/active-upstream.conf"
NETWORK=${NETWORK:-couponops}
PROXY=${PROXY:-couponops-proxy}
SA_PASSWORD=${SA_PASSWORD:-'Local_Dev_P@ssw0rd!'}
ADMIN_PASSWORD=${ADMIN_PASSWORD:-admin1234}
PROXY_URL=${PROXY_URL:-http://localhost:8080}
READY_TIMEOUT=${READY_TIMEOUT:-90}

IMAGE=${1:?사용법: deploy.sh <이미지> [--simulate-failure]}
SIMULATE_FAILURE=${2:-}

log()  { printf '\033[1;34m[deploy]\033[0m %s\n' "$*"; }
fail() { printf '\033[1;31m[deploy]\033[0m %s\n' "$*" >&2; }
ok()   { printf '\033[1;32m[deploy]\033[0m %s\n' "$*"; }

# ── 현재 색깔 판별 ───────────────────────────────────────────────────────────
CURRENT=$(grep -o 'couponops-app-[a-z]*' "$UPSTREAM_FILE" | head -1 | sed 's/couponops-app-//')
if [ "$CURRENT" = "blue" ]; then NEXT=green; NEXT_PORT=8082; else NEXT=blue; NEXT_PORT=8081; fi

CURRENT_CONTAINER="couponops-app-$CURRENT"
NEXT_CONTAINER="couponops-app-$NEXT"

log "현재: $CURRENT  →  배포 대상: $NEXT ($IMAGE)"

# ── 1. 스키마 적용 ───────────────────────────────────────────────────────────
# 앱보다 먼저, 단 한 번만. 새 인스턴스가 옛 스키마로 뜨면 그 자체가 장애다.
log "스키마 적용"
docker run --rm --network "$NETWORK" --entrypoint /app/efbundle "$IMAGE" \
  --connection "Server=mssql,1433;Database=CouponOps;User Id=sa;Password=$SA_PASSWORD;TrustServerCertificate=True" \
  >/dev/null

# ── 2. 새 인스턴스 기동 ──────────────────────────────────────────────────────
REDIS_HOST="redis:6379"
if [ "$SIMULATE_FAILURE" = "--simulate-failure" ]; then
  # 롤백 경로 시연용. 닿을 수 없는 Redis 를 주면 새 인스턴스는 준비 상태가 되지 못한다.
  log "※ 실패 시뮬레이션 — 새 인스턴스가 의도적으로 준비되지 않습니다"
  REDIS_HOST="unreachable-redis:6379"
fi

docker rm -f "$NEXT_CONTAINER" >/dev/null 2>&1 || true
docker run -d --name "$NEXT_CONTAINER" --network "$NETWORK" \
  --label "couponops.color=$NEXT" \
  -p "$NEXT_PORT:8080" \
  -e "ConnectionStrings__SqlServer=Server=mssql,1433;Database=CouponOps;User Id=sa;Password=$SA_PASSWORD;TrustServerCertificate=True;Max Pool Size=200" \
  -e "ConnectionStrings__Redis=$REDIS_HOST,abortConnect=false,connectTimeout=1000" \
  -e "Admin__SeedPassword=$ADMIN_PASSWORD" \
  "$IMAGE" >/dev/null

log "새 인스턴스 기동됨 ($NEXT_CONTAINER, 포트 $NEXT_PORT)"

# ── 3. 준비 대기 — 트래픽 전환의 게이트 ──────────────────────────────────────
# 경과 시간으로 센다. 반복 횟수로 세면 curl 왕복 시간 때문에 실제 대기가
# 의도한 시간을 크게 넘어선다(응답이 느릴 때 특히).
log "준비 상태 대기 (최대 ${READY_TIMEOUT}초)"
READY=0
DEADLINE=$(( $(date +%s) + READY_TIMEOUT ))
STARTED_AT=$(date +%s)

while [ "$(date +%s)" -lt "$DEADLINE" ]; do
  if curl -fsS --max-time 2 "http://localhost:$NEXT_PORT/health/ready" >/dev/null 2>&1; then
    ok "준비 완료 ($(( $(date +%s) - STARTED_AT ))초)"
    READY=1
    break
  fi
  sleep 1
done

if [ "$READY" -ne 1 ]; then
  fail "새 인스턴스가 준비되지 않았습니다. 트래픽을 전환하지 않습니다."
  fail "마지막 상태: $(curl -s --max-time 2 "http://localhost:$NEXT_PORT/health/ready" || echo '응답 없음')"
  docker logs --tail 15 "$NEXT_CONTAINER" 2>&1 | sed 's/^/    /' || true
  docker rm -f "$NEXT_CONTAINER" >/dev/null 2>&1 || true
  fail "롤백 완료 — 트래픽은 $CURRENT 에 그대로 있습니다."
  exit 1
fi

# ── 4. 트래픽 전환 ───────────────────────────────────────────────────────────
# reload 는 기존 연결을 끊지 않고 새 워커로 넘긴다. 이것이 무중단의 실체다.
log "트래픽 전환: $CURRENT → $NEXT"
cat > "$UPSTREAM_FILE" <<EOF
# 배포 스크립트가 이 파일을 덮어쓴다. 수동으로 편집하지 말 것.
upstream couponops_active {
    server $NEXT_CONTAINER:8080;
}
EOF
docker exec "$PROXY" nginx -s reload

# ── 5. 스모크 테스트 ─────────────────────────────────────────────────────────
# 인스턴스가 혼자 건강한 것과 프록시를 통해 실제로 응답하는 것은 다른 문제다.
log "프록시 경유 스모크 테스트"
SMOKE_OK=1
for _ in 1 2 3; do
  curl -fsS --max-time 5 "$PROXY_URL/health/ready" >/dev/null 2>&1 || SMOKE_OK=0
  sleep 1
done

if [ "$SMOKE_OK" -ne 1 ]; then
  fail "스모크 테스트 실패 — $CURRENT 로 되돌립니다."
  cat > "$UPSTREAM_FILE" <<EOF
upstream couponops_active {
    server $CURRENT_CONTAINER:8080;
}
EOF
  docker exec "$PROXY" nginx -s reload
  docker rm -f "$NEXT_CONTAINER" >/dev/null 2>&1 || true
  fail "롤백 완료 — 트래픽은 $CURRENT 에 있습니다."
  exit 1
fi

# ── 6. 옛 인스턴스 정리 ──────────────────────────────────────────────────────
# 전환 직후 바로 죽이지 않는다. 처리 중이던 요청이 끝날 시간을 준다.
log "옛 인스턴스 정리 ($CURRENT_CONTAINER)"
sleep 3
docker rm -f "$CURRENT_CONTAINER" >/dev/null 2>&1 || true

ok "배포 완료: $NEXT ($IMAGE)"

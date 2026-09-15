#!/usr/bin/env bash
# 부하 테스트 환경을 깨끗한 상태로 되돌린다.
# 측정 사이에 이전 실행의 데이터가 남아 있으면 인덱스 크기·버퍼풀 상태가 달라져 비교가 흐려진다.
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
PIDFILE=/tmp/couponops-lt.pid
SA_PW=${SA_PW:-'Local_Dev_P@ssw0rd!'}
CONN="Server=localhost,1433;Database=CouponOps;User Id=sa;Password=$SA_PW;TrustServerCertificate=True;Max Pool Size=200"

log() { printf '\033[1;34m[reset]\033[0m %s\n' "$*"; }

# 1. 앱 정지
# pidfile 만 믿지 않는다. 포트를 실제로 붙들고 있는 프로세스를 찾아 죽이고,
# 포트가 풀릴 때까지 기다린다 — 바로 재기동하면 "address already in use" 로 실패한다.
stop_app() {
  # ss 가 출력을 내주지 않는 환경이 있어 포트로 프로세스를 찾는 방법은 신뢰할 수 없다.
  # 프로세스 이름으로 죽이고, 포트가 실제로 응답을 멈출 때까지 기다린다.
  # (스크립트 파일로 실행되므로 이 bash 의 명령줄은 패턴에 걸리지 않는다)
  pkill -f 'dotnet CouponOps.Web.dll' 2>/dev/null || true
  rm -f "$PIDFILE"

  for _ in $(seq 1 30); do
    curl -sf -o /dev/null --max-time 1 http://127.0.0.1:5080/Account/Login || return 0
    sleep 1
  done

  pkill -9 -f 'dotnet CouponOps.Web.dll' 2>/dev/null || true
  sleep 2
}
stop_app

# 2. DB 재생성
log "DB 재생성"
docker exec lt-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SA_PW" -C -Q \
  "IF DB_ID('CouponOps') IS NOT NULL BEGIN ALTER DATABASE CouponOps SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE CouponOps; END" >/dev/null

# 3. Redis 비우기
log "Redis FLUSHALL"
docker exec lt-redis redis-cli FLUSHALL >/dev/null

# 4. 마이그레이션
log "마이그레이션 적용"
(cd "$ROOT" && PATH="$PATH:/root/.dotnet/tools" dotnet ef database update \
  -p src/CouponOps.Web -s src/CouponOps.Web --connection "$CONN" >/dev/null 2>&1)

# 5. 앱 기동 (Release)
log "앱 기동 (Release)"
cd /tmp/couponops-pub
nohup env ASPNETCORE_ENVIRONMENT=Production \
  Logging__LogLevel__Default=Warning \
  ConnectionStrings__SqlServer="$CONN" \
  ConnectionStrings__Redis="localhost:6379" \
  dotnet CouponOps.Web.dll --urls http://127.0.0.1:5080 > /tmp/couponops-lt.log 2>&1 &
echo $! > "$PIDFILE"

for _ in $(seq 1 40); do
  if curl -sf -o /dev/null http://127.0.0.1:5080/Account/Login; then log "준비 완료"; exit 0; fi
  sleep 1
done
log "앱이 뜨지 않았습니다 — /tmp/couponops-lt.log 확인"; exit 1

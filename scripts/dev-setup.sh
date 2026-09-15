#!/usr/bin/env bash
# 개발/CI 컨테이너에 이 PoC를 빌드·테스트할 수 있는 환경을 갖춘다.
# 멱등하게 동작하며, 이미 준비된 항목은 건너뛴다.
#
# 이 스크립트가 필요한 이유:
#   Claude Code 웹 세션 컨테이너는 비활성 시 회수된다. 세션이 바뀌면 .NET SDK와
#   Docker 데몬이 사라지므로 매 세션 이 스크립트로 환경을 복원한다.
#   (로컬 개발자 PC에서는 보통 필요 없다 — docker-compose.yml 을 쓰면 된다.)
set -euo pipefail

log() { printf '\033[1;34m[dev-setup]\033[0m %s\n' "$*"; }

# ── 1. .NET 8 SDK ───────────────────────────────────────────────────────────
# Microsoft 공식 배포 호스트(builds.dotnet.microsoft.com)는 이 환경의 네트워크
# 정책이 차단한다. Ubuntu 24.04 공식 저장소가 dotnet-sdk-8.0 을 제공하므로 그쪽을 쓴다.
if command -v dotnet >/dev/null 2>&1; then
  log ".NET SDK 이미 설치됨: $(dotnet --version)"
else
  log ".NET 8 SDK 설치 중 (Ubuntu 저장소)"
  apt-get update -qq
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq dotnet-sdk-8.0
  log ".NET SDK 설치 완료: $(dotnet --version)"
fi

# ── 2. Docker 데몬 ──────────────────────────────────────────────────────────
# 컨테이너 안에 dockerd 바이너리는 있으나 데몬이 기동돼 있지 않다.
# overlay2 는 중첩 환경에서 실패하므로 vfs 로 지정한다(느리지만 안전).
if docker info >/dev/null 2>&1; then
  log "Docker 데몬 이미 실행 중: $(docker info --format '{{.ServerVersion}}')"
else
  log "Docker 데몬 기동 중 (storage-driver=vfs)"
  mkdir -p /etc/docker
  [ -f /etc/docker/daemon.json ] || echo '{ "storage-driver": "vfs" }' > /etc/docker/daemon.json
  nohup env HTTP_PROXY="${HTTP_PROXY:-}" HTTPS_PROXY="${HTTPS_PROXY:-}" NO_PROXY="${NO_PROXY:-}" \
    dockerd > /tmp/dockerd.log 2>&1 &
  for _ in $(seq 1 30); do docker info >/dev/null 2>&1 && break; sleep 1; done
  docker info >/dev/null 2>&1 || { log "Docker 기동 실패 — /tmp/dockerd.log 확인"; exit 1; }
  log "Docker 데몬 준비 완료"
fi

# ── 3. 테스트용 이미지 ──────────────────────────────────────────────────────
# Docker Hub 는 이미지 config blob 다운로드가 차단된다(manifest 는 통과, blob 은 403).
# 따라서 MSSQL 은 허용된 mcr.microsoft.com 에서 받고, Redis 는 로컬에서 직접 빌드한다.
MSSQL_IMAGE="mcr.microsoft.com/mssql/server:2022-latest"
REDIS_IMAGE="coupon-ops/redis:7"

if docker image inspect "$MSSQL_IMAGE" >/dev/null 2>&1; then
  log "MSSQL 이미지 캐시됨"
else
  log "MSSQL 이미지 pull 중 (~1.6GB)"
  docker pull -q "$MSSQL_IMAGE"
fi

if docker image inspect "$REDIS_IMAGE" >/dev/null 2>&1; then
  log "Redis 이미지 캐시됨"
else
  log "Redis 7 이미지 빌드 중 (Docker Hub 차단 우회)"
  build_dir=$(mktemp -d)
  cat > "$build_dir/Dockerfile" <<'DOCKERFILE'
FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-noble
RUN apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends redis-server \
 && rm -rf /var/lib/apt/lists/*
EXPOSE 6379
ENTRYPOINT ["redis-server", "--protected-mode", "no", "--save", "", "--appendonly", "no"]
DOCKERFILE
  docker build -q --build-arg http_proxy="${HTTP_PROXY:-}" --build-arg https_proxy="${HTTPS_PROXY:-}" \
    -t "$REDIS_IMAGE" "$build_dir" >/dev/null
  rm -rf "$build_dir"
fi

# ── 4. k6 (부하 테스트) ─────────────────────────────────────────────────────
# 공식 배포 채널(dl.k6.io)은 이 환경의 네트워크 정책이 차단한다. GitHub 릴리스에서 받는다.
K6_VERSION=v0.54.0
if command -v k6 >/dev/null 2>&1; then
  log "k6 이미 설치됨: $(k6 version | head -1)"
else
  log "k6 설치 중 ($K6_VERSION)"
  tmp=$(mktemp -d)
  curl -sSL -o "$tmp/k6.tar.gz" \
    "https://github.com/grafana/k6/releases/download/$K6_VERSION/k6-$K6_VERSION-linux-amd64.tar.gz"
  tar xzf "$tmp/k6.tar.gz" -C "$tmp"
  install -m755 "$tmp/k6-$K6_VERSION-linux-amd64/k6" /usr/local/bin/k6
  rm -rf "$tmp"
  log "k6 설치 완료: $(k6 version | head -1)"
fi

log "환경 준비 완료. 통합 테스트 실행 전 다음 환경변수가 필요하다:"
log "  export TESTCONTAINERS_RYUK_DISABLED=true   # ryuk 이미지는 Docker Hub 에 있어 받을 수 없다"
log "  export COUPONOPS_TEST_MSSQL_IMAGE=$MSSQL_IMAGE"
log "  export COUPONOPS_TEST_REDIS_IMAGE=$REDIS_IMAGE"

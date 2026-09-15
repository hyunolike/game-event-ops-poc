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
  log "Docker 데몬 기동 중"
  mkdir -p /etc/docker
  # overlay2 를 먼저 쓴다. vfs 는 레이어를 통째로 복사해 디스크를 수십 배 쓰고,
  # 레이어가 깊어지면 빌드가 "failed to prepare ... invalid argument" 로 실패한다.
  # 중첩 환경에서 overlay2 가 안 되는 경우에만 vfs 로 내려간다.
  [ -f /etc/docker/daemon.json ] || echo '{ "storage-driver": "overlay2" }' > /etc/docker/daemon.json
  nohup env HTTP_PROXY="${HTTP_PROXY:-}" HTTPS_PROXY="${HTTPS_PROXY:-}" NO_PROXY="${NO_PROXY:-}" \
    dockerd > /tmp/dockerd.log 2>&1 &
  for _ in $(seq 1 30); do docker info >/dev/null 2>&1 && break; sleep 1; done

  if ! docker info >/dev/null 2>&1; then
    log "overlay2 기동 실패 — vfs 로 재시도"
    echo '{ "storage-driver": "vfs" }' > /etc/docker/daemon.json
    nohup env HTTP_PROXY="${HTTP_PROXY:-}" HTTPS_PROXY="${HTTPS_PROXY:-}" NO_PROXY="${NO_PROXY:-}" \
      dockerd >> /tmp/dockerd.log 2>&1 &
    for _ in $(seq 1 30); do docker info >/dev/null 2>&1 && break; sleep 1; done
  fi

  docker info >/dev/null 2>&1 || { log "Docker 기동 실패 — /tmp/dockerd.log 확인"; exit 1; }
  log "Docker 데몬 준비 완료 (storage-driver=$(docker info --format '{{.Driver}}'))"
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
# ENTRYPOINT 가 아니라 CMD 를 쓴다. ENTRYPOINT 로 두면 docker-compose 의 command 가
# 덮어쓰지 못하고 뒤에 덧붙어 인자가 깨진다(공식 redis 이미지와 같은 규약).
CMD ["redis-server", "--protected-mode", "no", "--save", "", "--appendonly", "no"]
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

# ── 5. 로컬 검증용 nginx 이미지 ─────────────────────────────────────────────
# docker-compose 의 프록시도 Docker Hub(nginx:alpine)를 쓰므로 같은 이유로 막힌다.
NGINX_IMAGE_LOCAL="coupon-ops/nginx:local"
if docker image inspect "$NGINX_IMAGE_LOCAL" >/dev/null 2>&1; then
  log "nginx 이미지 캐시됨"
else
  log "nginx 이미지 빌드 중 (Docker Hub 차단 우회)"
  build_dir=$(mktemp -d)
  cat > "$build_dir/Dockerfile" <<'DOCKERFILE'
FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-noble
RUN apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends nginx \
 && rm -rf /var/lib/apt/lists/* \
 && ln -sf /dev/stdout /var/log/nginx/access.log \
 && ln -sf /dev/stderr /var/log/nginx/error.log
# 공식 nginx 이미지와 같은 규약: /etc/nginx/conf.d/*.conf 를 읽는다.
RUN rm -f /etc/nginx/sites-enabled/default
EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]
DOCKERFILE
  docker build -q --network=host \
    --build-arg http_proxy="${HTTP_PROXY:-}" --build-arg https_proxy="${HTTPS_PROXY:-}" \
    -t "$NGINX_IMAGE_LOCAL" "$build_dir" >/dev/null
  rm -rf "$build_dir"
fi

log "환경 준비 완료. 통합 테스트 실행 전 다음 환경변수가 필요하다:"
log "  export TESTCONTAINERS_RYUK_DISABLED=true   # ryuk 이미지는 Docker Hub 에 있어 받을 수 없다"
log "  export COUPONOPS_TEST_MSSQL_IMAGE=$MSSQL_IMAGE"
log "  export COUPONOPS_TEST_REDIS_IMAGE=$REDIS_IMAGE"
log ""
log "docker compose 로 전체 기동할 때는 (Docker Hub 차단 우회):"
log "  REDIS_IMAGE=$REDIS_IMAGE NGINX_IMAGE=$NGINX_IMAGE_LOCAL docker compose up -d"

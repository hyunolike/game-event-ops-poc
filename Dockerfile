# ── 빌드 ────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 사설 CA 를 쓰는 네트워크(사내 프록시 등)를 위한 훅.
# certs/ 가 비어 있으면 아무 일도 일어나지 않는다 — CI 에서는 무동작이다.
COPY certs/ /usr/local/share/ca-certificates/
RUN update-ca-certificates

# 프로젝트 파일만 먼저 복사해 restore 한다. 소스만 바뀐 경우 이 레이어가 캐시되어
# 빌드 시간이 크게 줄어든다.
COPY .config/dotnet-tools.json .config/
COPY src/CouponOps.Web/CouponOps.Web.csproj src/CouponOps.Web/
RUN dotnet restore src/CouponOps.Web/CouponOps.Web.csproj \
 && dotnet tool restore

COPY src/ src/
RUN dotnet publish src/CouponOps.Web/CouponOps.Web.csproj -c Release -o /app/publish --no-restore

# 마이그레이션을 자기완결 실행 파일로 묶는다.
# 배포 시점에 SDK 나 소스가 필요 없어지고, 이미지와 스키마 버전이 함께 움직인다.
RUN dotnet ef migrations bundle \
      --project src/CouponOps.Web/CouponOps.Web.csproj \
      --startup-project src/CouponOps.Web/CouponOps.Web.csproj \
      --configuration Release --no-build --force \
      --output /app/publish/efbundle

# ── 실행 ────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1
EXPOSE 8080

# 비 root 로 실행한다. .NET 8 이미지가 제공하는 app 사용자(UID 1654).
USER $APP_UID

# liveness 를 쓴다 — 의존성이 잠깐 흔들린다고 컨테이너를 재시작시키면 안 된다.
# 트래픽 투입 판단(readiness)은 배포 스크립트와 로드밸런서의 몫이다.
# curl·wget 을 설치하지 않는다. 앱 자신이 --healthcheck 로 프로브 역할을 한다 —
# 이미지가 작아지고, 빌드가 외부 apt 저장소에 의존하지 않는다.
HEALTHCHECK --interval=10s --timeout=5s --start-period=15s --retries=3 \
  CMD ["dotnet", "CouponOps.Web.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "CouponOps.Web.dll"]

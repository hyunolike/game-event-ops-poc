#!/usr/bin/env bash
# 부하 테스트용 룰렛을 하나 만들고 ID 를 표준출력에 찍는다.
#   사용법: provision-draw.sh <한정경품재고> [이름접두사]
#
# 생성 폼은 시뮬레이션 게이트를 거쳐야 저장된다(가중치 오타 방어).
# 스크립트도 예외가 아니므로 Preview → 서명 추출 → Create 순서를 그대로 따른다.
set -euo pipefail

BASE=${BASE_URL:-http://127.0.0.1:5080}
STOCK=${1:?한정 경품 재고가 필요합니다}
PREFIX=${2:-ld}

JAR=$(mktemp)
BODY=$(mktemp)
trap 'rm -f "$JAR" "$BODY"' EXIT

token() { curl -s -c "$JAR" -b "$JAR" "$1" \
  | grep -o 'name="__RequestVerificationToken"[^>]*value="[^"]*"' | head -1 | sed 's/.*value="//;s/"$//'; }

T=$(token "$BASE/Account/Login")
curl -s -c "$JAR" -b "$JAR" -o /dev/null -X POST "$BASE/Account/Login" \
  --data-urlencode "LoginId=${ADMIN_ID:-admin}" \
  --data-urlencode "Password=${ADMIN_PW:-admin1234}" \
  --data-urlencode "__RequestVerificationToken=$T"

CODE="$PREFIX-$(date +%s%N | tail -c 9)"

# 슬롯 구성 — 한정 경품의 가중치를 넉넉히 줘야 짧은 측정 구간 안에 재고가 소진되고,
# 그래야 "소진 이후에도 초과 지급이 없다" 를 실제로 확인할 수 있다.
#   0: 한정 상자 (weight 500,  재고 $STOCK, 잭팟, 소진 시 슬롯 2 로 대체)
#   1: 강화 주문서 (weight 1500, 무제한)
#   2: 골드        (weight 8000, 무제한)  ← 대체 대상
form() {
  printf '%s\n' \
    "--data-urlencode" "Code=$CODE" \
    "--data-urlencode" "Name=부하테스트 룰렛 $CODE" \
    "--data-urlencode" "StartsAt=$(date -u -d '+9 hours -10 minutes' +%Y-%m-%dT%H:%M)" \
    "--data-urlencode" "EndsAt=$(date -u -d '+9 hours +6 hours' +%Y-%m-%dT%H:%M)" \
    "--data-urlencode" "DailyDrawLimit=1000000" \
    "--data-urlencode" "DailyResetHour=4" \
    "--data-urlencode" "TicketItemId=" \
    "--data-urlencode" "TicketCost=0" \
    "--data-urlencode" "PityThreshold=0" \
    "--data-urlencode" "PitySlot=-1" \
    "--data-urlencode" "FallbackSlot=2" \
    "--data-urlencode" "SimulationDraws=100000" \
    "--data-urlencode" "Slots[0].Name=한정 상자" \
    "--data-urlencode" "Slots[0].ItemId=9001" \
    "--data-urlencode" "Slots[0].ItemQty=1" \
    "--data-urlencode" "Slots[0].Weight=500" \
    "--data-urlencode" "Slots[0].Stock=$STOCK" \
    "--data-urlencode" "Slots[0].IsJackpot=true" \
    "--data-urlencode" "Slots[0].IsBlank=false" \
    "--data-urlencode" "Slots[1].Name=강화 주문서" \
    "--data-urlencode" "Slots[1].ItemId=3001" \
    "--data-urlencode" "Slots[1].ItemQty=5" \
    "--data-urlencode" "Slots[1].Weight=1500" \
    "--data-urlencode" "Slots[1].Stock=-1" \
    "--data-urlencode" "Slots[1].IsJackpot=false" \
    "--data-urlencode" "Slots[1].IsBlank=false" \
    "--data-urlencode" "Slots[2].Name=골드" \
    "--data-urlencode" "Slots[2].ItemId=1001" \
    "--data-urlencode" "Slots[2].ItemQty=1000" \
    "--data-urlencode" "Slots[2].Weight=8000" \
    "--data-urlencode" "Slots[2].Stock=-1" \
    "--data-urlencode" "Slots[2].IsJackpot=false" \
    "--data-urlencode" "Slots[2].IsBlank=false"
}

# 1) 시뮬레이션 — 게이트 서명을 받아 온다.
T=$(token "$BASE/Draws/Create")
mapfile -t ARGS < <(form)
curl -s -c "$JAR" -b "$JAR" -o "$BODY" -X POST "$BASE/Draws/Create?handler=Preview" \
  "${ARGS[@]}" --data-urlencode "__RequestVerificationToken=$T"

SIG=$(grep -o 'name="PreviewSignature"[^>]*value="[^"]*"' "$BODY" | head -1 | sed 's/.*value="//;s/"$//')
if [ -z "$SIG" ]; then
  echo "시뮬레이션 게이트 서명을 받지 못했습니다. 응답 일부:" >&2
  head -c 600 "$BODY" >&2
  exit 1
fi

# 2) 생성 — 같은 설정 + 서명.
T=$(token "$BASE/Draws/Create")
LOCATION=$(curl -s -c "$JAR" -b "$JAR" -o /dev/null -D - -X POST "$BASE/Draws/Create?handler=Create" \
  "${ARGS[@]}" \
  --data-urlencode "PreviewSignature=$SIG" \
  --data-urlencode "__RequestVerificationToken=$T" \
  | grep -i '^location:' | tr -d '\r')

if [ -z "$LOCATION" ]; then
  echo "룰렛 생성에 실패했습니다(리다이렉트 없음)." >&2
  exit 1
fi

echo "$LOCATION" | sed 's/.*id=//'

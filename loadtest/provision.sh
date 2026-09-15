#!/usr/bin/env bash
# 부하 테스트용 이벤트를 하나 만들고 ID 를 표준출력에 찍는다.
#   사용법: provision.sh <수량> <유저당한도> [이름접두사]
set -euo pipefail

BASE=${BASE_URL:-http://127.0.0.1:5080}
QTY=${1:?수량이 필요합니다}
LIMIT=${2:-1}
PREFIX=${3:-lt}

JAR=$(mktemp)
trap 'rm -f "$JAR"' EXIT

token() { curl -s -c "$JAR" -b "$JAR" "$1" \
  | grep -o 'name="__RequestVerificationToken"[^>]*value="[^"]*"' | head -1 | sed 's/.*value="//;s/"$//'; }

T=$(token "$BASE/Account/Login")
curl -s -c "$JAR" -b "$JAR" -o /dev/null -X POST "$BASE/Account/Login" \
  --data-urlencode "LoginId=${ADMIN_ID:-admin}" \
  --data-urlencode "Password=${ADMIN_PW:-admin1234}" \
  --data-urlencode "__RequestVerificationToken=$T"

CODE="$PREFIX-$(date +%s%N | tail -c 9)"
T=$(token "$BASE/Events/Create")
LOCATION=$(curl -s -c "$JAR" -b "$JAR" -o /dev/null -D - -X POST "$BASE/Events/Create" \
  --data-urlencode "Code=$CODE" \
  --data-urlencode "Name=부하테스트 $CODE" \
  --data-urlencode "StartsAt=$(date -u -d '+9 hours -10 minutes' +%Y-%m-%dT%H:%M)" \
  --data-urlencode "EndsAt=$(date -u -d '+9 hours +6 hours' +%Y-%m-%dT%H:%M)" \
  --data-urlencode "TotalQuantity=$QTY" \
  --data-urlencode "PerUserLimit=$LIMIT" \
  --data-urlencode "IssuanceMode=PreGenerated" \
  --data-urlencode "__RequestVerificationToken=$T" \
  | grep -i '^location:' | tr -d '\r')

echo "$LOCATION" | sed 's/.*id=//'

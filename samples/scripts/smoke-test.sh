#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# End-to-end smoke test for the sample stack.
#
#   ./scripts/smoke-test.sh            # brings the stack up, asserts, tears it down
#   KEEP_STACK=1 ./scripts/smoke-test.sh   # leaves it running for inspection
#
# This runs in CI so the sample cannot rot silently: it asserts the behaviour the
# README claims, using the origin's own request counters as the source of truth.
# ---------------------------------------------------------------------------
set -euo pipefail

cd "$(dirname "$0")/.."

ORIGIN=http://localhost:5080
CLIENT=http://localhost:5081
CONTROL=http://localhost:5083

failures=0

log()  { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
pass() { printf '  \033[0;32m[pass]\033[0m %s\n' "$*"; }
fail() { printf '  \033[0;31m[FAIL]\033[0m %s\n' "$*"; failures=$((failures + 1)); }

cleanup() {
  if [[ "${KEEP_STACK:-0}" == "1" ]]; then
    log "KEEP_STACK=1 — leaving the stack running"
    return
  fi
  log "Tearing the stack down"
  docker compose down -v --remove-orphans >/dev/null 2>&1 || true
}
trap cleanup EXIT

# Value of a numeric JSON property, 0 when absent. The keys here contain spaces and
# slashes but never quotes or nesting, so grep is enough — and keeps this script
# dependent on nothing but curl.
json_number() {
  local url=$1 key=$2 value
  value=$(curl -fsS "$url" | grep -o "\"${key}\":[0-9]\+" | head -1 | cut -d: -f2)
  echo "${value:-0}"
}

# Counter value for a given endpoint from the origin's /stats.
origin_count() {
  json_number "$ORIGIN/stats" "$1"
}

# Fire N concurrent GETs at a URL and wait for all of them.
burst() {
  local url=$1 count=$2
  seq "$count" | xargs -P "$count" -I{} curl -fsS -o /dev/null "$url" || true
}

log "Building and starting the stack"
docker compose up -d --build --wait --wait-timeout 300

log "Waiting for the opening stampede and the feature tour to finish"
deadline=$((SECONDS + 180))
until docker compose logs client-a 2>/dev/null | grep -q "feature tour complete"; do
  if (( SECONDS > deadline )); then
    fail "client-a never finished the feature tour"
    docker compose logs --tail 60 client-a
    break
  fi
  sleep 3
done

# ---------------------------------------------------------------------------
log "1. The feature tour verified every documented behaviour"
tour_line=$(docker compose logs client-a 2>/dev/null | grep -o "feature tour complete: [0-9]*/[0-9]* checks" | tail -1 || true)
if [[ -z "$tour_line" ]]; then
  fail "no feature tour summary in client-a's logs"
elif [[ "$tour_line" =~ ([0-9]+)/([0-9]+) ]] && [[ "${BASH_REMATCH[1]}" == "${BASH_REMATCH[2]}" ]]; then
  pass "$tour_line"
else
  fail "$tour_line — some scenario did not behave as documented"
  docker compose logs client-a 2>/dev/null | grep "\[??\]" || true
fi

# ---------------------------------------------------------------------------
log "2. Coalescing collapses a concurrent burst into one origin call"
before=$(origin_count "GET /slow")
burst "$CLIENT/api/slow" 20
after=$(origin_count "GET /slow")
delta=$((after - before))
# The entry may already be warm from the scripted workload, so 0 is also correct.
if (( delta <= 1 )); then
  pass "20 concurrent callers → $delta origin call(s)"
else
  fail "20 concurrent callers → $delta origin calls (expected at most 1)"
fi

# ---------------------------------------------------------------------------
log "3. The control instance shows what that costs without Stampede.Http"
before=$(origin_count "GET /slow")
burst "$CONTROL/api/slow" 20
after=$(origin_count "GET /slow")
control_delta=$((after - before))
if (( control_delta >= 10 )); then
  pass "20 concurrent callers with no coalescing → $control_delta origin calls"
else
  fail "control instance produced only $control_delta origin calls (expected ≥ 10) — is Stampede.Http leaking into the baseline?"
fi

# ---------------------------------------------------------------------------
log "4. Vary keeps one cache entry per representation"
before=$(origin_count "GET /greetings")
for lang in en-GB es-ES fr-FR; do
  curl -fsS -o /dev/null "$CLIENT/api/greetings?lang=$lang"
  curl -fsS -o /dev/null "$CLIENT/api/greetings?lang=$lang"
done
after=$(origin_count "GET /greetings")
delta=$((after - before))
if (( delta <= 3 )); then
  pass "3 languages fetched twice each → $delta origin call(s)"
else
  fail "3 languages fetched twice each → $delta origin calls (expected at most 3)"
fi

# ---------------------------------------------------------------------------
log "5. The instruments are exported for Prometheus"
if curl -fsS "$CLIENT/metrics" | grep -q "stampede_http"; then
  pass "client exposes stampede_http.* on /metrics"
else
  fail "no stampede_http instruments on $CLIENT/metrics"
fi

if curl -fsS "$ORIGIN/metrics" | grep -q "sample_api_origin_requests"; then
  pass "origin exposes its request counter on /metrics"
else
  fail "no origin request counter on $ORIGIN/metrics"
fi

if curl -fsS "http://localhost:9090/api/v1/query?query=up" | grep -q '"status":"success"'; then
  pass "Prometheus is scraping"
else
  fail "Prometheus is not answering queries"
fi

# ---------------------------------------------------------------------------
log "6. The cache is actually being hit"
hits=$(json_number "$CLIENT/api/counters" "stampede_http.cache.hits")
if (( hits > 0 )); then
  pass "client-a served $hits requests from cache"
else
  fail "client-a reports zero cache hits"
fi

# ---------------------------------------------------------------------------
if (( failures == 0 )); then
  log "All smoke checks passed"
else
  log "$failures smoke check(s) failed"
fi
exit $(( failures > 0 ? 1 : 0 ))

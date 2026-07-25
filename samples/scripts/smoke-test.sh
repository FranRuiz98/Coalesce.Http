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

# Poll a condition until it holds or the budget runs out.
#
# Metric endpoints do not become correct at the instant a container reports healthy:
# an OpenTelemetry counter emits nothing until it has recorded its first measurement,
# and the exporter caches scrape responses briefly. Asserting on that with a single
# request is a race, and it is the race that first broke this script in CI.
retry_until() {
  local deadline=$((SECONDS + $1)); shift
  while ! "$@"; do
    if (( SECONDS >= deadline )); then
      return 1
    fi
    sleep 2
  done
}

# Whether an endpoint's body contains a pattern. Used as the predicate for retry_until.
body_contains() {
  curl -fsS "$1" 2>/dev/null | grep -q "$2"
}

# Whether Prometheus reports the origin's scrape target as up.
prometheus_origin_target_up() {
  curl -fsS --get "http://localhost:9090/api/v1/query" \
    --data-urlencode 'query=up{job="sample-api"}' 2>/dev/null \
    | grep -q '"value":\[[0-9.]*,"1"\]'
}

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
# 0 is also correct: the entry may already be warm from the scripted workload. And the
# workload's own /slow burst can land inside this window and contribute one call of its
# own, so allow 2 — still decisive against the 20 the control instance produces below.
if (( delta <= 2 )); then
  pass "20 concurrent callers → $delta origin call(s)"
else
  fail "20 concurrent callers → $delta origin calls (expected at most 2)"
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
if retry_until 60 body_contains "$CLIENT/metrics" "stampede_http"; then
  pass "client exposes stampede_http.* on /metrics"
else
  fail "no stampede_http instruments on $CLIENT/metrics after 60 s"
  printf '       what it returned instead:\n'
  curl -fsS "$CLIENT/metrics" 2>&1 | head -5 | sed 's/^/       /'
fi

if retry_until 60 body_contains "$ORIGIN/metrics" "sample_api_origin_requests"; then
  pass "origin exposes its request counter on /metrics"
else
  fail "no origin request counter on $ORIGIN/metrics after 60 s"
  printf '       what it returned instead:\n'
  curl -fsS "$ORIGIN/metrics" 2>&1 | head -5 | sed 's/^/       /'
fi

# Stronger than "Prometheus answers queries": assert the origin target is actually up,
# which is what the dashboards depend on. Prometheus scrapes every 5 s, so give it room
# to have completed a first round.
if retry_until 60 prometheus_origin_target_up; then
  pass "Prometheus is scraping the origin"
else
  fail "Prometheus has no healthy sample-api target after 60 s"
  printf '       target health:\n'
  curl -fsS "http://localhost:9090/api/v1/targets?state=active" 2>&1 \
    | tr ',' '\n' | grep -E '"(scrapeUrl|health|lastError)"' | sed 's/^/       /' | head -20
fi

# ---------------------------------------------------------------------------
log "6. The cache is actually being hit"
retry_until 60 body_contains "$CLIENT/api/counters" '"stampede_http.cache.hits"' || true
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

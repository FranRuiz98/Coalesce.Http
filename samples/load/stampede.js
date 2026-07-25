// ---------------------------------------------------------------------------
// Load profile for the Stampede.Http sample.
//
//   docker compose --profile load up k6
//
// Two identical arrival patterns run side by side: one against the client with
// Stampede.Http in its pipeline, one against the control instance without it.
// Because both drive the same origin, the origin's own counters at the end of the
// run are a controlled measurement rather than a marketing claim.
// ---------------------------------------------------------------------------

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';

const STAMPEDE = __ENV.STAMPEDE_TARGET || 'http://localhost:5081';
const BASELINE = __ENV.BASELINE_TARGET || 'http://localhost:5083';

// Served straight from the client cache — no origin involvement.
const cacheHits = new Counter('client_cache_hits');

const LANGUAGES = ['en-GB', 'es-ES', 'fr-FR'];
const TENANTS = ['acme', 'globex', 'initech'];

const STAGES = [
  { duration: '20s', target: 40 },  // ramp into the stampede
  { duration: '60s', target: 40 },  // steady state
  { duration: '20s', target: 0 },   // drain
];

export const options = {
  scenarios: {
    stampede: {
      executor: 'ramping-vus',
      exec: 'browse',
      stages: STAGES,
      env: { TARGET: STAMPEDE },
      tags: { mode: 'stampede' },
    },
    baseline: {
      executor: 'ramping-vus',
      exec: 'browse',
      stages: STAGES,
      env: { TARGET: BASELINE },
      tags: { mode: 'baseline' },
      startTime: '0s',
    },
  },
  thresholds: {
    // The clients must stay up under load regardless of which pipeline they run.
    http_req_failed: ['rate<0.05'],
    // Both limits are deliberately loose — they exist so the summary prints the two
    // latency distributions side by side. That contrast, and the origin counters in
    // teardown(), are the actual output of this run.
    'http_req_duration{mode:stampede}': ['p(95)<3000'],
    'http_req_duration{mode:baseline}': ['p(95)<10000'],
  },
};

/** One virtual user's browsing session against whichever client this scenario targets. */
export function browse() {
  const target = __ENV.TARGET;
  const roll = Math.random();

  let response;
  if (roll < 0.55) {
    response = http.get(`${target}/api/catalog`, { tags: { endpoint: 'catalog' } });
  } else if (roll < 0.7) {
    response = http.get(`${target}/api/feed`, { tags: { endpoint: 'feed' } });
  } else if (roll < 0.82) {
    const lang = LANGUAGES[Math.floor(Math.random() * LANGUAGES.length)];
    response = http.get(`${target}/api/greetings?lang=${lang}`, { tags: { endpoint: 'greetings' } });
  } else if (roll < 0.94) {
    const tenant = TENANTS[Math.floor(Math.random() * TENANTS.length)];
    response = http.get(`${target}/api/tenants/${tenant}`, { tags: { endpoint: 'tenants' } });
  } else {
    // The expensive one. With coalescing a burst of these collapses into a single
    // origin call; without it, every caller waits out the origin's 2 s.
    response = http.get(`${target}/api/slow`, { tags: { endpoint: 'slow' } });
  }

  check(response, { 'status is 200': (r) => r.status === 200 });

  // The client echoes the origin's Age header back in its JSON payload; a non-null
  // value means the caller never touched the origin.
  try {
    if (response.json('ageSeconds') !== null) {
      cacheHits.add(1);
    }
  } catch {
    // Non-JSON body (an error page under load) — not worth failing the run over.
  }

  sleep(Math.random() * 0.5);
}

/** Prints the origin's own view of the run: how much traffic each client actually caused. */
export function teardown() {
  const stats = http.get(`${STAMPEDE}/api/origin-stats`);
  console.log('Origin counters after the run (shared across every client):');
  console.log(JSON.stringify(stats.json(), null, 2));
  console.log(
    'Compare in Prometheus: sum by (client) (rate(sample_api_origin_requests_total[1m]))'
  );
}

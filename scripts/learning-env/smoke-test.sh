#!/usr/bin/env bash
# Calls the real endpoints and checks status codes and response shapes.
source "$(dirname "$0")/common.sh"

require_command curl ""
require_command node "Install Node.js 22."

check() {
  local method=$1 path=$2 expected=$3 body=${4:-}
  local args=(-s -o /tmp/syspro-smoke.$$ -w '%{http_code}' -X "$method" "$API_URL$path")
  [[ -n "$body" ]] && args+=(-H 'Content-Type: application/json' -d "$body")
  local status
  status=$(curl "${args[@]}") || fail "$method $path: no response from $API_URL"
  [[ "$status" == "$expected" ]] || { cat /tmp/syspro-smoke.$$ >&2; fail "$method $path returned $status, expected $expected"; }
  info "$method $path -> $status"
}

check GET "/api/customers/totals?fromDate=2000-01-01T00:00:00Z&toDate=2100-01-01T00:00:00Z" 200
check GET "/api/customers/totals?fromDate=2026-02-01T00:00:00Z&toDate=2026-01-01T00:00:00Z" 400
check GET "/api/orders/2147483647" 404
check GET "/api/import/jobs/2147483647" 404
check POST "/api/orders" 400 '{"currency":"x","status":"","items":[]}'
node -e '
  const body = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"));
  for (const key of ["type", "title", "status", "errors", "traceId"]) {
    if (!(key in body)) { console.error("ValidationProblemDetails lacks " + key); process.exit(1); }
  }
' /tmp/syspro-smoke.$$
rm -f /tmp/syspro-smoke.$$
info "Smoke test passed"

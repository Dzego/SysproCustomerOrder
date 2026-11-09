#!/usr/bin/env bash
# Every frontend check that needs no backend: lint, types, formatting, Jest, the
# production build and the stubbed Cypress suite. Stops at the first failure.
source "$(dirname "$0")/common.sh"

use_node22
cd "$REPO_ROOT/syspro-web"
[[ -d node_modules ]] || fail "Dependencies are not installed. Run: (cd syspro-web && npm ci)"

info "Lint";              npm run -s lint
info "TypeScript";        npm run -s typecheck
info "Prettier";          npm run -s format:check
info "Jest";              npx jest --ci
info "Production build";  npx ng build --configuration production

started_server=""
if ! curl -s -o /dev/null "$WEB_URL"; then
  info "Starting the Angular dev server for Cypress"
  npx ng serve --port 4200 >"$REPO_ROOT/.learning-env/ng-serve.log" 2>&1 &
  started_server=$!
  trap '[[ -n "$started_server" ]] && kill "$started_server" 2>/dev/null' EXIT
  for _ in $(seq 1 90); do curl -s -o /dev/null "$WEB_URL" && break; sleep 1; done
  curl -s -o /dev/null "$WEB_URL" || fail "The dev server did not start; see .learning-env/ng-serve.log"
fi

info "Cypress (stubbed API)"
unset ELECTRON_RUN_AS_NODE
npx cypress run --e2e

info "All frontend checks passed"

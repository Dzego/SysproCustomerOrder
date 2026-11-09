#!/usr/bin/env bash
# Runs the full-stack Cypress suite against the real API and the learning database.
# Prerequisites: db-start.sh, db-migrate.sh and api-start.sh (in another terminal).
# To start from an empty database, stop the API, run db-reset.sh, and start the API again.
source "$(dirname "$0")/common.sh"

use_node22
require_learning_db_running
require_api

"$(dirname "$0")/smoke-test.sh"

cd "$REPO_ROOT/syspro-web"
curl -s -o /dev/null "$WEB_URL" || fail "The Angular dev server is not running on $WEB_URL. Start it with: (cd syspro-web && npm start)"

info "Cypress (real API and SQL Server)"
unset ELECTRON_RUN_AS_NODE
CYPRESS_SUITE=fullstack npx cypress run --e2e

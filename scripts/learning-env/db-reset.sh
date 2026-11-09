#!/usr/bin/env bash
# Drops and recreates the learning database, then migrates it. Only for the learning
# container: it checks the container's label and the database name first.
source "$(dirname "$0")/common.sh"

require_learning_db_running

label=$(docker inspect --format '{{index .Config.Labels "syspro.environment"}}' "$LEARNING_CONTAINER")
[[ "$label" == "learning" ]] || fail "Refusing to reset: $LEARNING_CONTAINER is not the labelled learning container."
[[ "$LEARNING_DATABASE" == "SysproLearningDb" ]] || fail "Refusing to reset a database other than SysproLearningDb."

if [[ "${1:-}" != "--yes" ]]; then
  read -r -p "Delete all data in $LEARNING_DATABASE inside $LEARNING_CONTAINER? Type 'reset' to continue: " answer
  [[ "$answer" == "reset" ]] || fail "Cancelled."
fi

info "Dropping $LEARNING_DATABASE"
learning_sql "IF DB_ID('$LEARNING_DATABASE') IS NOT NULL BEGIN ALTER DATABASE [$LEARNING_DATABASE] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$LEARNING_DATABASE]; END" >/dev/null

"$(dirname "$0")/db-migrate.sh"

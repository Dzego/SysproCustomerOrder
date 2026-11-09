#!/usr/bin/env bash
# Runs the real ASP.NET Core API in Development against the learning database, in the
# foreground (stop it with Ctrl+C). The import reads the deterministic fixture file.
source "$(dirname "$0")/common.sh"

require_learning_db_running
load_secrets
require_command dotnet "Install the .NET 8 SDK."

if curl -s -o /dev/null "$API_URL" 2>/dev/null; then
  fail "Something is already listening on $API_URL. Stop it first."
fi

# A separate content root holds copies of the API's settings and the fixture, so the
# repository's own LegacyData file is never replaced.
rm -rf "$API_CONTENT_ROOT"
mkdir -p "$API_CONTENT_ROOT/LegacyData"
cp "$REPO_ROOT/Syspro.Api/appsettings.json" "$REPO_ROOT/Syspro.Api/appsettings.Development.json" "$API_CONTENT_ROOT/"
cp "$REPO_ROOT/scripts/learning-env/fixtures/customers_legacy.dat" "$API_CONTENT_ROOT/LegacyData/"

connection="$(learning_connection_string)"
[[ "$connection" == *"localhost,$LEARNING_PORT;Database=$LEARNING_DATABASE;"* ]] ||
  fail "Refusing to start: the connection string does not name the learning database."

info "Starting the API on $API_URL against $LEARNING_DATABASE (port $LEARNING_PORT)"
# Environment variables take precedence over user secrets, so the API cannot fall back
# to the original database configured there.
ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_URLS="$API_URL" \
ConnectionStrings__DefaultConnection="$connection" \
CustomerImportWorker__PollInterval="00:00:01" \
  exec dotnet run --project "$REPO_ROOT/Syspro.Api" --no-launch-profile -- --contentRoot "$API_CONTENT_ROOT"

#!/usr/bin/env bash
# Applies the existing EF Core migrations to the learning database only.
source "$(dirname "$0")/common.sh"

require_learning_db_running
load_secrets
require_command dotnet "Install the .NET 8 SDK."
dotnet ef --version >/dev/null 2>&1 || fail "dotnet-ef is missing. Install it with: dotnet tool install --global dotnet-ef --version 8.0.30"

info "Applying migrations to $LEARNING_DATABASE on port $LEARNING_PORT"
# The connection string is passed as an environment variable, which takes precedence over
# user secrets and appsettings (so the original database is never used) and, unlike
# --connection, keeps the password out of the process list.
ASPNETCORE_ENVIRONMENT=Development \
ConnectionStrings__DefaultConnection="$(learning_connection_string)" \
  dotnet ef database update --project "$REPO_ROOT/Syspro.Api"

applied=$(learning_sql "SET NOCOUNT ON; SELECT COUNT(*) FROM [$LEARNING_DATABASE].dbo.__EFMigrationsHistory")
info "$LEARNING_DATABASE has $applied migrations applied"

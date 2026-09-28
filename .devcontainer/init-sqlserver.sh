#!/usr/bin/env bash
set -euo pipefail

for attempt in {1..60}; do
    if /opt/mssql-tools18/bin/sqlcmd \
        -S sqlserver -U sa -P "$MSSQL_SA_PASSWORD" -C -b \
        -Q "IF DB_ID(N'TripDB') IS NULL CREATE DATABASE [TripDB]" \
        >/dev/null 2>&1; then
        echo 'TripDB ready'
        exit 0
    fi
    sleep 2
done

echo 'Could not initialize TripDB' >&2
exit 1

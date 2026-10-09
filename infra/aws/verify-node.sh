#!/usr/bin/env bash
# Read-only verification. Does not start experiments or invoke models.
set -euo pipefail
cd /opt/incidentlab/current
docker compose -f compose.monitor.yml ps
docker compose -f compose.monitor.yml exec -T postgres pg_isready -U incidentlab -d incidentlab
docker compose -f compose.monitor.yml exec -T opensearch curl -fsS http://localhost:9200/_cluster/health
docker compose -f compose.monitor.yml exec -T monitor dotnet IncidentLab.Api.dll --verify

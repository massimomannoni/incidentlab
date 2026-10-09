#!/usr/bin/env bash
# Trusted-origin diagnostics only. Does not solve/disable WAF CAPTCHA or issue a session.
set -euo pipefail
umask 077
lab_release=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)
[[ "$lab_release" == /opt/incidentlab/releases/* && $(id -u) == 0 ]]
# The installer validates these assignments and creates this root-only file.
set -a
source "$lab_release/.env.public"
set +a
lab_curl_config=$(mktemp /tmp/incidentlab-public-check.XXXXXX)
trap 'rm -f -- "$lab_curl_config"' EXIT
# Keep origin credentials out of process arguments and Run Command output.
printf 'header = "Host: %s"\nheader = "X-IncidentLab-Origin: %s"\n' "$PUBLIC_HOST" "$PUBLIC_ORIGIN_KEY" > "$lab_curl_config"
lab_url="http://$MONITOR_BIND_ADDRESS:8020"
for lab_attempt in {1..30}; do
  lab_status=$(curl --silent --output /dev/null --write-out '%{http_code}' --max-time 3 "$lab_url/api/overview" || true)
  [[ "$lab_status" == 403 ]] && break
  sleep 1
done
[[ "$lab_status" == 403 ]]
[[ $(curl --silent --output /dev/null --write-out '%{http_code}' --max-time 5 --config "$lab_curl_config" "$lab_url/api/overview") == 401 ]]
[[ $(curl --silent --output /dev/null --write-out '%{http_code}' --max-time 5 --config "$lab_curl_config" "$lab_url/login") == 403 ]]
[[ $(curl --silent --output /dev/null --write-out '%{http_code}' --max-time 5 --config "$lab_curl_config" "$lab_url/") == 302 ]]
echo 'Origin secret required; anonymous API blocked; login without CAPTCHA proof blocked; browser redirected. No session issued.'

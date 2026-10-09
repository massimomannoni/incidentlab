#!/usr/bin/env bash
set -euo pipefail
umask 077
[[ $# == 2 && $(id -u) == 0 ]]
lab_region=$1; lab_parameter=$2
lab_release=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)
[[ "$lab_release" == /opt/incidentlab/releases/* ]]
lab_secret_file=$(mktemp /tmp/incidentlab-public.XXXXXX)
lab_env_file=$(mktemp "$lab_release/.env.public.XXXXXX")
trap 'rm -f -- "$lab_secret_file" "$lab_env_file"' EXIT
aws --region "$lab_region" ssm get-parameter --name "$lab_parameter" --with-decryption --output json > "$lab_secret_file"
jq -er '
  if .Parameter.Type != "SecureString" then error("invalid") else .Parameter.Value | fromjson end
  | del(.PUBLIC_READY_DIGEST)
  | if .PUBLIC_ACCESS != "viewer" then error("public administration is disabled") else . end
  | ["PUBLIC_HOST","PUBLIC_ORIGIN_KEY","PUBLIC_USERNAME","PUBLIC_PASSWORD_HASH","PUBLIC_PASSWORD_SALT","PUBLIC_ACCESS","MONITOR_BIND_ADDRESS"] as $base
  | ["PUBLIC_MASTER_USERNAME","PUBLIC_MASTER_PASSWORD_HASH","PUBLIC_MASTER_PASSWORD_SALT"] as $master
  | if (keys != ($base | sort) and keys != (($base + $master) | sort) and keys != (($base + $master + ["PUBLIC_MASTER_ADMISSION_KEY"]) | sort))
       or any(.[]; type != "string" or (test("^[A-Za-z0-9._-]+$") | not)) then error("invalid") else . end
  | to_entries | sort_by(.key) | .[] | "\(.key)=\(.value)"
' "$lab_secret_file" > "$lab_env_file" 2>/dev/null
chmod 0600 "$lab_env_file"
mv -f -- "$lab_env_file" "$lab_release/.env.public"
cd -- "$lab_release"
docker compose --env-file .env.public -f compose.public.yml config --quiet
docker compose --env-file .env.public -f compose.public.yml build
docker compose --env-file .env.public -f compose.public.yml up -d --wait --wait-timeout 120
bash infra/aws/verify-public.sh
if [[ -e /opt/incidentlab/public-current && ! -L /opt/incidentlab/public-current ]]; then exit 1; fi
ln -sfn -- "$lab_release" /opt/incidentlab/public-current
echo 'Public gateway installed; no credentials printed.'

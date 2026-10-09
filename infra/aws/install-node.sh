#!/usr/bin/env bash
# Invoked as root through SSM, from a checksummed release archive.
set -euo pipefail
umask 077
if [[ $# != 7 ]]; then echo 'Expected role, region, parameter, Compose version/SHA256, Buildx version/SHA256' >&2; exit 2; fi
lab_role=$1; lab_region=$2; lab_parameter=$3
lab_compose_version=$4; lab_compose_sha=$5; lab_buildx_version=$6; lab_buildx_sha=$7
[[ "$lab_role" == monitor || "$lab_role" == target ]]
[[ $(id -u) == 0 && $(uname -m) == x86_64 ]]
[[ "$lab_compose_version" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ && "$lab_compose_sha" =~ ^[a-f0-9]{64}$ ]]
[[ "$lab_buildx_version" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ && "$lab_buildx_sha" =~ ^[a-f0-9]{64}$ ]]
cloud-init status --wait >/dev/null
command -v aws >/dev/null
systemctl is-active --quiet docker
lab_release=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
[[ "$lab_release" == /opt/incidentlab/releases/* ]]
if [[ -e /opt/incidentlab/current && ! -L /opt/incidentlab/current ]]; then
  echo 'Refusing to replace a non-symlink /opt/incidentlab/current' >&2; exit 1
fi

install_plugin() {
  local lab_name=$1 lab_url=$2 lab_sha=$3 lab_download
  lab_download=$(mktemp /tmp/incidentlab-plugin.XXXXXX)
  curl --fail --show-error --silent --location --retry 3 "$lab_url" -o "$lab_download"
  if ! printf '%s  %s\n' "$lab_sha" "$lab_download" | sha256sum --check --status; then
    echo "Checksum mismatch: $lab_name; downloaded file retained at $lab_download" >&2; exit 1
  fi
  install -d -m 0755 /usr/local/lib/docker/cli-plugins
  install -m 0755 "$lab_download" "/usr/local/lib/docker/cli-plugins/$lab_name"
  rm -- "$lab_download"
}
install_plugin docker-compose "https://github.com/docker/compose/releases/download/$lab_compose_version/docker-compose-linux-x86_64" "$lab_compose_sha"
install_plugin docker-buildx "https://github.com/docker/buildx/releases/download/$lab_buildx_version/buildx-$lab_buildx_version.linux-amd64" "$lab_buildx_sha"
docker compose version
docker buildx version
if [[ "$lab_role" == monitor ]]; then
  [[ $(sysctl -n vm.max_map_count) -ge 262144 ]]
fi

# Never put values in Run Command arguments, console output or shell tracing.
lab_secret_file=$(mktemp /tmp/incidentlab-config.XXXXXX)
lab_env_file=$(mktemp "$lab_release/.env.XXXXXX")
trap 'rm -f -- "$lab_secret_file" "$lab_env_file"' EXIT
aws --region "$lab_region" ssm get-parameter --name "$lab_parameter" --with-decryption --output json > "$lab_secret_file"
if ! jq -er --arg role "$lab_role" -f "$lab_release/infra/aws/write-env.jq" "$lab_secret_file" > "$lab_env_file" 2>/dev/null; then
  echo 'Invalid installation configuration (values withheld)' >&2; exit 1
fi
chmod 0600 "$lab_env_file"
mv -f -- "$lab_env_file" "$lab_release/.env"
cd -- "$lab_release"
docker compose -f "compose.$lab_role.yml" config --quiet
# Build first. No volumes are deleted; Compose project names are stable across releases.
docker compose -f "compose.$lab_role.yml" build
docker compose -f "compose.$lab_role.yml" up -d --wait --wait-timeout 480
ln -sfn -- "$lab_release" /opt/incidentlab/current
docker compose -f "compose.$lab_role.yml" ps
echo "Installed $lab_role. From your operator checkout, run bash scripts/lab.sh aws verify --apply after BOTH hosts have started."

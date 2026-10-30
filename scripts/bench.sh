#!/usr/bin/env bash
# Runs the full membench test plan against a server from the clients image (only Docker needed here)
# and saves the report in ./membench-results/.
#   scripts/bench.sh <server>            e.g. scripts/bench.sh 10.0.0.5   or   scripts/bench.sh 10.0.0.5:8088
# Without Docker, run the standalone binary instead: dist/<platform>/membench <server>
set -euo pipefail
SERVER=${1:?usage: $0 <server-ip or host:port>}
here=$(cd "$(dirname "$0")/.." && pwd)
if [ "${REBUILD:-0}" = 1 ] || ! docker image inspect memserve-clients:latest >/dev/null 2>&1; then
    docker build -q -t memserve-clients:latest -f "$here/docker/clients/Dockerfile" "$here" >/dev/null
fi
mkdir -p membench-results
exec docker run --rm --network host -v "$PWD/membench-results:/work/membench-results" -w /work \
    -v /etc/localtime:/etc/localtime:ro \
    --user "$(id -u):$(id -g)" memserve-clients:latest membench "$SERVER"

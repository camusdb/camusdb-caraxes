#!/usr/bin/env bash
# PostgreSQL twin of the single-node CamusDB bank arms (k117-*-tmpfs-n1): the same table, 2,000 rows, 50/50 point
# reads and serializable transfers, closed loop, one database container with its data on tmpfs and no CPU cap, and
# the load generator in a second container reaching it over TCP on a private Docker network.
#
#   tools/pgbench-bank/run.sh <out_dir> <warmup_s> <measure_s> <clients> [<clients> ...]
#
# Each client count gets a fresh server and a fresh load, a discarded warm-up, then the measured run, with the exact
# CPU time of both containers read from their cgroups around the window. After the run it checks the bank
# invariants: SUM(balance) is unchanged and SUM(version) is twice the committed transfers.
# Env: PG_IMAGE (postgres:18), PG_MEMORY (8g), PG_TMPFS (6g), PG_SHARED_BUFFERS (2GB), PG_MODE (prepared).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=$(realpath -m "$1"); warm=$2; meas=$3; shift 3
img=${PG_IMAGE:-postgres:18}; mem=${PG_MEMORY:-8g}; tmpfs=${PG_TMPFS:-6g}
sb=${PG_SHARED_BUFFERS:-2GB}; mode=${PG_MODE:-prepared}
net=pgbank; srv=pgbank-server; cli=pgbank-client; rows=2000
mkdir -p "$out"

cpu_usec() { awk '/^usage_usec/{print $2}' "/sys/fs/cgroup/system.slice/docker-$(docker inspect -f '{{.Id}}' "$1").scope/cpu.stat"; }
psql_q() { docker exec "$srv" psql -h 127.0.0.1 -U postgres -d bank -Atq -c "$1"; }
cleanup() { docker rm -f "$srv" "$cli" >/dev/null 2>&1 || true; }
trap cleanup EXIT
docker network inspect "$net" >/dev/null 2>&1 || docker network create "$net" >/dev/null

for c in "$@"; do
  d="$out/c$c"; rm -rf "$d"; mkdir -p "$d"; j=$(( c < 16 ? c : 16 ))
  # The same quiet-host bar as checks.require_quiet_host: wait for the 1-minute load to fall below 2.
  until awk '{exit !($1 < 2)}' /proc/loadavg; do sleep 5; done
  echo "$(date +%T) c=$c: ambient load $(cut -d' ' -f1 /proc/loadavg)"
  cleanup
  docker run -d --name "$srv" --network "$net" --memory "$mem" --shm-size 1g --tmpfs "/pgdata:rw,size=$tmpfs" \
    -e PGDATA=/pgdata/data -e POSTGRES_HOST_AUTH_METHOD=trust -e POSTGRES_DB=bank \
    "$img" -c shared_buffers="$sb" -c max_connections=600 >/dev/null
  until docker exec "$srv" pg_isready -q -h 127.0.0.1 -U postgres -d bank 2>/dev/null; do sleep 1; done
  docker exec -i "$srv" psql -h 127.0.0.1 -U postgres -d bank -q -v ON_ERROR_STOP=1 < "$here/schema.sql"
  docker run -d --name "$cli" --network "$net" -v "$here":/scripts:ro -v "$d":/out "$img" sleep infinity >/dev/null
  bench() { docker exec "$cli" pgbench -h "$srv" -U postgres -n -M "$mode" -c "$c" -j "$j" --max-tries=10 \
      -D nclients="$c" -D nrows="$rows" -f /scripts/read.sql@50 -f /scripts/transfer.sql@50 "$@" bank; }
  bench -T "$warm" > "$d/warmup.txt" 2>&1
  v0=$(psql_q "SELECT sum(version) FROM workload_accounts")
  s0=$(cpu_usec "$srv"); k0=$(cpu_usec "$cli")
  bench -T "$meas" -P 10 -r --log --sampling-rate=0.05 --log-prefix=/out/latency > "$d/pgbench.txt" 2>&1
  s1=$(cpu_usec "$srv"); k1=$(cpu_usec "$cli")
  {
    echo "version_before=$v0"
    echo "version_after=$(psql_q "SELECT sum(version) FROM workload_accounts")"
    echo "balance_sum=$(psql_q "SELECT sum(balance) FROM workload_accounts")"
    echo "row_count=$(psql_q "SELECT count(*) FROM workload_accounts")"
    echo "server_cpu_usec=$((s1 - s0))"
    echo "client_cpu_usec=$((k1 - k0))"
    echo "server_memory_peak=$(cat "/sys/fs/cgroup/system.slice/docker-$(docker inspect -f '{{.Id}}' "$srv").scope/memory.peak")"
    echo "server_version=$(psql_q "SELECT version()")"
    echo "clients=$c threads=$j mode=$mode warmup_s=$warm measure_s=$meas memory=$mem shared_buffers=$sb image=$img"
  } > "$d/facts.txt"
  psql_q "SELECT name || '=' || setting FROM pg_settings ORDER BY name" > "$d/settings.txt"
  python3 -I "$here/summarize.py" "$d"
done

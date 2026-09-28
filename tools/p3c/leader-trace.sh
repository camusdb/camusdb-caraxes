#!/usr/bin/env bash
# Leader + follower EventPipe trace under the tmpfs learned bank load (Kommander feature fc4dabe7, task 2).
# Runs scenarios/bank-rebase-cand-p1-w128-tmpfs-rel182-learned-trace.yml (6-minute warm-up) on the existing image
# (--skip-build) and, inside the warm-up, takes a 10 s smoke trace of the partition leader, a 60 s trace of the
# leader, then a 60 s trace of one follower, so the measured window is untouched. The tool is dotnet-trace's
# platform-agnostic net8.0 build run under DOTNET_ROLL_FORWARD=Major against PID 1, as the gcdump recipe on
# CamusDB 80af367a / efa86ffa does. Profiles: dotnet-sampled-thread-time (per-thread stack samples) + dotnet-common
# (GC, JIT, exceptions, threading). `cpu-sampling` in dotnet-trace 10 is collect-linux only (kernel perf), which
# neither the container nor this host allows.
#
#   TOOL=<dir holding dotnet-trace.dll> TAG=tr1 tools/p3c/leader-trace.sh
#
# Output: runs/scenarios/<scenario>-<tag>/diagnostics/{leader,follower}-<node>.nettrace plus a /metrics scrape of the
# traced node before and after each trace; the driver log is runs/leader-trace-driver-<tag>.log.
set -u
cd ~/camusdb-caraxes || exit 1
tag=${TAG:-tr1}
tool=${TOOL:?set TOOL to the directory holding dotnet-trace.dll}
scenario=bank-rebase-cand-p1-w128-tmpfs-rel182-learned-trace
cluster=banktmpfsrel182trace
log=~/camusdb-caraxes/runs/leader-trace-driver-$tag.log
out=~/camusdb-caraxes/runs/scenarios/$scenario-$tag/diagnostics
note() { echo "$(date -Is) $*" >> "$log"; }
quiet_wait() {
  local cores deadline load per
  cores=$(nproc); deadline=$(( $(date +%s) + 900 ))
  while :; do
    load=$(awk '{print $1}' /proc/loadavg); per=$(awk -v l="$load" -v c="$cores" 'BEGIN{print (l/c)}')
    awk -v p="$per" 'BEGIN{exit !(p < 0.25)}' && return 0
    [ "$(date +%s)" -ge "$deadline" ] && { note "quiet_wait gave up at load $load"; return 0; }
    sleep 20
  done
}
port() { echo $((15094 + ${1#camus})); }
scrape() { curl -s --max-time 10 "http://localhost:$(port "$1")/metrics" > "$out/metrics-$2.txt"; }
trace() { # $1 = node, $2 = label, $3 = duration dd:hh:mm:ss
  local c="$cluster-$1"
  scrape "$1" "$2-$1-before"
  note "trace $2 on $1 start"
  docker exec -e DOTNET_ROLL_FORWARD=Major "$c" dotnet /diag/dotnet-trace.dll collect -p 1 --duration "$3" \
    --profile dotnet-sampled-thread-time,dotnet-common -o "/diag/$2-$1.nettrace" < /dev/null >> "$log" 2>&1
  note "trace $2 on $1 end exit=$?"
  scrape "$1" "$2-$1-after"
  docker cp "$c:/diag/$2-$1.nettrace" "$out/" >> "$log" 2>&1
  docker exec "$c" rm -f "/diag/$2-$1.nettrace"
}

note "=== START leader-trace ==="
quiet_wait
dotnet run --project Caraxes -c Release -- run --scenario "scenarios/$scenario.yml" --tag "$tag" --skip-build >> "$log" 2>&1 &
rpid=$!
until grep -q '==> running workload' "$log"; do
  kill -0 "$rpid" 2>/dev/null || { note "run exited before the workload started"; exit 1; }
  sleep 2
done
note "workload started (warm-up 6m begins)"
mkdir -p "$out"
leader=$(grep -A1 'waiting up to .* for partition leadership' "$log" | tail -1 | grep -o 'camus[0-9]=1' | head -1 | cut -d= -f1)
[ -n "$leader" ] || { note "could not resolve the partition leader"; wait "$rpid"; exit 1; }
n=${leader#camus}; follower=camus$(( n % 3 + 1 ))
note "leader=$leader follower=$follower"
for node in "$leader" "$follower"; do
  docker exec "$cluster-$node" mkdir -p /diag
  docker cp "$tool/." "$cluster-$node:/diag/" >> "$log" 2>&1
done
sleep 30
trace "$leader" smoke 00:00:00:10
sleep 50
trace "$leader" leader 00:00:01:00
sleep 5
trace "$follower" follower 00:00:01:00
note "traces done; waiting for the run"
wait "$rpid"
note "=== END leader-trace exit=$? ==="

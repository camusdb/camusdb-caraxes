#!/usr/bin/env bash
# Leader + follower allocation-sampled EventPipe traces and a leader gcdump under the tmpfs learned bank load
# (CamusDB feature ffbda1b5 "Phase 4", task 6ba448df). Companion of leader-trace.sh, which takes the CPU
# (sampled-thread-time) traces; this one takes the `gc-verbose` profile (GC events + AllocationTick every ~100 KB,
# with stacks) so allocations can be attributed by component and by site, then a gcdump of the leader's live heap.
# Same scenario (6-minute warm-up), image and --skip-build; every capture ends inside the warm-up so the measured
# window is the unperturbed control.
#
#   TRACE_TOOL=<dir holding dotnet-trace.dll> GCDUMP_TOOL=<dir holding dotnet-gcdump.dll> TAG=alloc1 tools/p3c/alloc-trace.sh
#   CAPTURES="follower-alloc leader-gcdump" ... (default: leader-alloc follower-alloc leader-gcdump, in that order)
#
# Every capture runs under `timeout`: on alloc1 the follower's session never flushed (6 KB file) and dotnet-trace sat
# through the whole measured window until the teardown killed it, so the gcdump behind it never ran.
#
# Output: runs/scenarios/<scenario>-<tag>/diagnostics/{leader,follower}-alloc-<node>.nettrace, leader-<node>.gcdump
# and a /metrics scrape of the traced node before and after each capture; driver log runs/alloc-trace-driver-<tag>.log.
set -u
cd ~/camusdb-caraxes || exit 1
tag=${TAG:-alloc1}
trace_tool=${TRACE_TOOL:?set TRACE_TOOL to the directory holding dotnet-trace.dll}
gcdump_tool=${GCDUMP_TOOL:?set GCDUMP_TOOL to the directory holding dotnet-gcdump.dll}
scenario=bank-rebase-cand-p1-w128-tmpfs-rel182-learned-trace
cluster=banktmpfsrel182trace
log=~/camusdb-caraxes/runs/alloc-trace-driver-$tag.log
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
  timeout -k 10 150 docker exec -e DOTNET_ROLL_FORWARD=Major "$c" dotnet /diag/trace/dotnet-trace.dll collect -p 1 --duration "$3" \
    --profile gc-verbose -o "/diag/$2-$1.nettrace" < /dev/null >> "$log" 2>&1
  note "trace $2 on $1 end exit=$?"
  scrape "$1" "$2-$1-after"
  docker cp "$c:/diag/$2-$1.nettrace" "$out/" >> "$log" 2>&1
  docker exec "$c" rm -f "/diag/$2-$1.nettrace"
}
gcdump() { # $1 = node, $2 = label
  local c="$cluster-$1"
  scrape "$1" "$2-$1-before"
  note "gcdump $2 on $1 start"
  timeout -k 10 180 docker exec -e DOTNET_ROLL_FORWARD=Major "$c" dotnet /diag/gcdump/dotnet-gcdump.dll collect -p 1 \
    -o "/diag/$2-$1.gcdump" < /dev/null >> "$log" 2>&1
  note "gcdump $2 on $1 end exit=$?"
  scrape "$1" "$2-$1-after"
  docker cp "$c:/diag/$2-$1.gcdump" "$out/" >> "$log" 2>&1
  docker exec "$c" rm -f "/diag/$2-$1.gcdump"
}

note "=== START alloc-trace ==="
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
  docker exec "$cluster-$node" mkdir -p /diag/trace /diag/gcdump
  docker cp "$trace_tool/." "$cluster-$node:/diag/trace/" >> "$log" 2>&1
  docker cp "$gcdump_tool/." "$cluster-$node:/diag/gcdump/" >> "$log" 2>&1
done
sleep 30
for capture in ${CAPTURES:-leader-alloc follower-alloc leader-gcdump}; do
  case $capture in
    leader-alloc) trace "$leader" leader-alloc 00:00:01:00 ;;
    follower-alloc) trace "$follower" follower-alloc 00:00:01:00 ;;
    leader-gcdump) gcdump "$leader" leader ;;
    *) note "unknown capture $capture" ;;
  esac
  sleep 5
done
note "captures done; waiting for the run"
wait "$rpid"
note "=== END alloc-trace exit=$? ==="

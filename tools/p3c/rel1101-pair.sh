#!/usr/bin/env bash
# Shipped-stack pair on the tmpfs learned bank shape (CamusDB plan 84b26503, "Next run" 2026-09-27): Kahuna.Core 1.10.1 /
# Kommander 1.9.0 from nuget.org plus the CamusDB retryable-abort value path, ONE image (caraxes/camusdb:bankrel1101) built
# beforehand from ~/camusdb, both arms --skip-build.
#
#   arm 1  scenarios/bank-rel1101-tmpfs-learned-trace.yml   control: histogram off; inside the 6-minute warm-up a 10 s smoke
#          trace and a 60 s EventPipe trace of the partition leader, then 60 s of one follower (leader-trace.sh recipe, every
#          capture under `timeout`), the unperturbed schedstat sampler over the whole run, and a /metrics scrape of the leader
#          near the end of the measured window.
#   arm 2  scenarios/bank-rel1101-tmpfs-learned-stages.yml  KOMMANDER_ROUND_STAGES=1: the per-stage round histogram.
#
#   TOOL=<dir holding dotnet-trace.dll> TAG=ss1 tools/p3c/rel1101-pair.sh
#
# Output: runs/scenarios/<scenario>-<tag>/ for each arm; arm 1 also gets diagnostics/{smoke,leader,follower}-<node>.nettrace,
# metrics-*-{before,after}.txt, metrics-leader-end.txt and schedstat-threads.csv. Driver log: runs/rel1101-pair-driver-<tag>.log.
set -u
cd ~/camusdb-caraxes || exit 1
tag=${TAG:-ss1}
tool=${TOOL:?set TOOL to the directory holding dotnet-trace.dll}
log=~/camusdb-caraxes/runs/rel1101-pair-driver-$tag.log
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

note "=== START rel1101 pair ==="
# Never overlap a measured window with a test run on this host. The test process is the test host process
# (there is no "testhost" in its command line, which is what let a first launch start under the suite); TESTLOG, when
# set, names a log that ends with a "TEST EXIT" line once the suite is done, and is the decisive guard.
while pgrep -f "[d]otnet exec .*CamusDB\.Tests" > /dev/null; do sleep 15; done
if [ -n "${TESTLOG:-}" ]; then until grep -q "TEST EXIT" "$TESTLOG" 2>/dev/null; do sleep 15; done; fi
while pgrep -f "[d]ocker build" > /dev/null; do sleep 15; done

# ---- arm 1: control + traces ------------------------------------------------------------------------------------------
scenario=bank-rel1101-tmpfs-learned-trace
cluster=bankrel1101trace
out=~/camusdb-caraxes/runs/scenarios/$scenario-$tag/diagnostics
scrape() { curl -s --max-time 10 "http://localhost:$(port "$1")/metrics" > "$out/metrics-$2.txt"; }
trace() { # $1 = node, $2 = label, $3 = duration dd:hh:mm:ss, $4 = timeout seconds
  local c="$cluster-$1"
  scrape "$1" "$2-$1-before"
  note "trace $2 on $1 start"
  timeout -k 10 "$4" docker exec -e DOTNET_ROLL_FORWARD=Major "$c" dotnet /diag/dotnet-trace.dll collect -p 1 --duration "$3" \
    --profile dotnet-sampled-thread-time,dotnet-common -o "/diag/$2-$1.nettrace" < /dev/null >> "$log" 2>&1
  note "trace $2 on $1 end exit=$?"
  scrape "$1" "$2-$1-after"
  docker cp "$c:/diag/$2-$1.nettrace" "$out/" >> "$log" 2>&1
  docker exec "$c" rm -f "/diag/$2-$1.nettrace"
}

quiet_wait
note "arm 1 ($scenario) start (load $(awk '{print $1}' /proc/loadavg))"
dotnet run --project Caraxes -c Release -- run --scenario "scenarios/$scenario.yml" --tag "$tag" --skip-build >> "$log" 2>&1 &
rpid=$!
until grep -q '==> running workload' "$log"; do
  kill -0 "$rpid" 2>/dev/null || { note "arm 1 exited before the workload started"; exit 1; }
  sleep 2
done
t0=$(date +%s)
note "workload started (warm-up 6m begins)"
mkdir -p "$out"
tools/traceattr/schedstat-sample.sh "$out/schedstat-threads.csv" "$cluster" 1080 &
spid=$!
leader=$(grep -A1 'waiting up to .* for partition leadership' "$log" | tail -1 | grep -o 'camus[0-9]=1' | head -1 | cut -d= -f1)
if [ -z "$leader" ]; then
  note "could not resolve the partition leader; no traces this arm"
else
  n=${leader#camus}; follower=camus$(( n % 3 + 1 ))
  note "leader=$leader follower=$follower"
  for node in "$leader" "$follower"; do
    docker exec "$cluster-$node" mkdir -p /diag
    docker cp "$tool/." "$cluster-$node:/diag/" >> "$log" 2>&1
  done
  sleep 30
  trace "$leader" smoke 00:00:00:10 60
  sleep 50
  trace "$leader" leader 00:00:01:00 150
  sleep 5
  trace "$follower" follower 00:00:01:00 150
  note "traces done"
  # A full scrape of the leader ~15.5 min after the workload started: inside the measured window, before the drain.
  until [ $(( $(date +%s) - t0 )) -ge 930 ]; do kill -0 "$rpid" 2>/dev/null || break; sleep 10; done
  scrape "$leader" leader-end
  note "end-of-window scrape of $leader done"
fi
wait "$rpid"
note "arm 1 end exit=$?"
wait "$spid" 2>/dev/null

# ---- arm 2: stages histogram --------------------------------------------------------------------------------------------
scenario=bank-rel1101-tmpfs-learned-stages
quiet_wait
note "arm 2 ($scenario) start (load $(awk '{print $1}' /proc/loadavg))"
dotnet run --project Caraxes -c Release -- run --scenario "scenarios/$scenario.yml" --tag "$tag" --skip-build >> "$log" 2>&1
note "arm 2 end exit=$?"
note "=== END rel1101 pair ==="

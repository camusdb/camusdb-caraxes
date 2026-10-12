#!/usr/bin/env bash
# CPU attribution arms: run scenarios one after another on an idle host (--skip-build, Caraxes already built in Release)
# and, inside each run's warm-up, take a 10 s smoke trace of the partition leader, a 60 s trace of the leader and a 60 s
# trace of one follower (dotnet-sampled-thread-time + dotnet-common, as leader-trace.sh did), then sample every node's
# per-thread schedstat through the measured window (tools/traceattr/schedstat-sample.sh, unperturbed CPU and run-queue
# wait). Every trace ends inside the warm-up, so the measured window is the plain control and the run's figure stands
# on its own. The scenario's warm-up must be >= 5 minutes.
#
#   SCENS="bank-rel1117c-tmpfs-tls-trace bank-rel1117c-tmpfs-clear-trace" TAG=tr2 \
#   TOOL=<dir holding dotnet-trace.dll> tools/p3c/cpu-trace-arms.sh
#
# Output per arm: runs/scenarios/<scenario>-<tag>/diagnostics/{smoke,leader,follower}-<node>.nettrace, a /metrics scrape of
# the traced node before and after each trace, schedstat.csv; Caraxes output in runs/cpu-trace-arm-<scenario>-<tag>.log;
# the driver's own notes in runs/cpu-trace-driver-<tag>.log. Read the traces with tools/traceattr (--components).
set -u
cd ~/camusdb-caraxes || exit 1
tag=${TAG:?set TAG}
scens=${SCENS:?set SCENS to a space-separated list of scenario names}
tool=${TOOL:?set TOOL to the directory holding dotnet-trace.dll}
warmup_s=${WARMUP_S:-360}
measure_s=${MEASURE_S:-600}
log=~/camusdb-caraxes/runs/cpu-trace-driver-$tag.log
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
busy_wait() {
  while pgrep -f "[d]otnet exec .*CamusDB\.Tests" > /dev/null; do sleep 15; done
  while pgrep -f "[d]ocker build" > /dev/null; do sleep 15; done
}
port() { echo $((15094 + ${1#camus})); }
scrape() { curl -s --max-time 10 "http://localhost:$(port "$1")/metrics" > "$out/metrics-$2.txt"; }
trace() { # $1 = node, $2 = label, $3 = duration dd:hh:mm:ss, $4 = host-side timeout in seconds
  local c="$cluster-$1"
  scrape "$1" "$2-$1-before"
  note "trace $2 on $1 start"
  timeout -k 10 "$4" docker exec -e DOTNET_ROLL_FORWARD=Major "$c" dotnet /diag/dotnet-trace.dll collect -p 1 --duration "$3" \
    --profile dotnet-sampled-thread-time,dotnet-common -o "/diag/$2-$1.nettrace" < /dev/null >> "$log" 2>&1
  local rc=$?
  note "trace $2 on $1 end exit=$rc"
  # A session that stalls (tr2: 32 KB after 150 s, dotnet-trace 10.0.750501 under load) keeps sampling inside the node after
  # the host-side client is killed, so the collector is killed in the container too; otherwise it would run into the window.
  if [ "$rc" -ne 0 ]; then
    docker exec "$c" sh -c 'for p in /proc/[0-9]*; do c=$(tr "\0" " " < $p/cmdline 2>/dev/null); case "$c" in "dotnet /diag/dotnet-trace.dll"*) kill ${p#/proc/};; esac; done'
    note "trace $2 on $1 killed inside the container"
  fi
  scrape "$1" "$2-$1-after"
  docker cp "$c:/diag/$2-$1.nettrace" "$out/" >> "$log" 2>&1
  docker exec "$c" rm -f "/diag/$2-$1.nettrace"
  return "$rc"
}

note "=== START cpu-trace arms: $scens ==="
for scenario in $scens; do
  cluster=$(awk '/^cluster:/{c=1;next} c&&/^  name:/{print $2;exit}' "scenarios/$scenario.yml")
  [ -n "$cluster" ] || { note "no cluster name in scenarios/$scenario.yml"; continue; }
  out=~/camusdb-caraxes/runs/scenarios/$scenario-$tag/diagnostics
  armlog=~/camusdb-caraxes/runs/cpu-trace-arm-$scenario-$tag.log
  busy_wait; quiet_wait
  note "arm $scenario start (cluster $cluster, load $(awk '{print $1}' /proc/loadavg))"
  dotnet run --project Caraxes -c Release --no-build -- run --scenario "scenarios/$scenario.yml" --tag "$tag" --skip-build >> "$armlog" 2>&1 &
  rpid=$!
  until grep -q '==> running workload' "$armlog"; do
    kill -0 "$rpid" 2>/dev/null || { note "arm $scenario exited before the workload started"; continue 2; }
    sleep 2
  done
  started=$(date +%s)
  note "workload started (warm-up ${warmup_s}s begins)"
  mkdir -p "$out"
  leader=$(grep -A1 'partition leadership to settle' "$armlog" | tail -1 | grep -o 'camus[0-9]=1' | head -1 | cut -d= -f1)
  if [ -z "$leader" ]; then
    note "could not resolve the partition leader; the arm runs untraced"
  else
    n=${leader#camus}; follower=camus$(( n % 3 + 1 ))
    note "leader=$leader follower=$follower"
    for node in "$leader" "$follower"; do
      docker exec "$cluster-$node" mkdir -p /diag
      docker cp "$tool/." "$cluster-$node:/diag/" >> "$log" 2>&1
    done
    sleep 30
    # The smoke capture is the gate: if the tool cannot finish 10 s under this load (60 s budget), the 60 s captures
    # would only stall into the measured window, so the arm runs on as an untraced control instead.
    if trace "$leader" smoke 00:00:00:10 60 && [ "$(stat -c %s "$out/smoke-$leader.nettrace" 2>/dev/null || echo 0)" -gt 1000000 ]; then
      sleep 20
      trace "$leader" leader 00:00:01:00 150
      sleep 5
      trace "$follower" follower 00:00:01:00 150
      note "traces done at +$(( $(date +%s) - started ))s"
    else
      note "smoke trace failed or stalled; leader/follower traces skipped, arm continues untraced"
    fi
  fi
  # schedstat through the measured window only: wait for the warm-up to end, then sample for its length.
  while [ $(( $(date +%s) - started )) -lt "$warmup_s" ]; do sleep 5; done
  note "schedstat sampling starts (measured window)"
  bash tools/traceattr/schedstat-sample.sh "$out/schedstat.csv" "$cluster" "$measure_s" >> "$log" 2>&1
  note "schedstat sampling done"
  wait "$rpid"
  note "arm $scenario end exit=$?"
done
note "=== END cpu-trace arms ==="

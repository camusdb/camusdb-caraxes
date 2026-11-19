#!/usr/bin/env bash
# CPU profile of the loaded partition leader with perf (EventPipe does not start there under load).
# Runs one scenario (--skip-build; its cluster.env must set DOTNET_PerfMapEnabled=1), and inside the measured window
# records the leader process for RECORD_S seconds from a privileged host-pid container holding perf.
#   SCEN=tp1-perf TAG=tp1 [WAIT_UNIT=caraxes-tp1] [RECORD_AT_S=480] [RECORD_S=60] [FREQ=299] tools/p3c/perf-leader-arm.sh
# Output: runs/scenarios/<scen>-<tag>/diagnostics/{perf.data,perf-1.map,leader.txt}; log runs/perf-arm-<scen>-<tag>.log
set -u
cd ~/camusdb-caraxes || exit 1
scen=${SCEN:?}; tag=${TAG:?}; at=${RECORD_AT_S:-480}; secs=${RECORD_S:-45}; freq=${FREQ:-199}
log=~/camusdb-caraxes/runs/perf-arm-$scen-$tag.log
out=~/camusdb-caraxes/runs/scenarios/$scen-$tag/diagnostics
if [ -n "${WAIT_UNIT:-}" ]; then while systemctl --user is-active -q "$WAIT_UNIT"; do sleep 20; done; sleep 60; fi
cluster=$(awk '/^cluster:/{c=1;next} c&&/^  name:/{print $2;exit}' "scenarios/$scen.yml")
dotnet run --project Caraxes -c Release --no-build -- run --scenario "scenarios/$scen.yml" --tag "$tag" --skip-build >> "$log" 2>&1 &
rpid=$!
until grep -q '==> running workload' "$log"; do kill -0 "$rpid" 2>/dev/null || exit 1; sleep 2; done
started=$(date +%s); mkdir -p "$out"
leader=$(grep -A1 'partition leadership to settle' "$log" | tail -1 | grep -o 'camus[0-9]=1' | head -1 | cut -d= -f1)
echo "$leader" > "$out/leader.txt"
while [ $(( $(date +%s) - started )) -lt "$at" ]; do sleep 5; done
pid=$(docker inspect -f '{{.State.Pid}}' "$cluster-$leader")
echo "$(date -Is) perf record leader=$leader hostpid=$pid for ${secs}s at ${freq}Hz" >> "$log"
docker cp "$cluster-$leader:/tmp/perf-1.map" "$out/perf-1.map" >> "$log" 2>&1
# Record, then symbolise in the same container while the node's files are still reachable through /proc/<pid>/root:
# native libraries by --symfs, JIT and ReadyToRun frames by the perf map copied to both names perf may look for.
docker run --rm --privileged --pid=host -v "$out:/out" local/perf:6.12 sh -c "
  perf record -o /out/perf.data -F $freq -g -p $pid -- sleep $secs
  cp /proc/$pid/root/tmp/perf-1.map /tmp/perf-$pid.map; cp /tmp/perf-$pid.map /tmp/perf-1.map
  perf script -i /out/perf.data --symfs /proc/$pid/root -F comm,tid,period,ip,sym,dso > /out/perf.script.txt 2>/out/perf.script.err
  chmod a+r /out/perf.data /out/perf.script.txt" >> "$log" 2>&1
echo "$pid" > "$out/hostpid.txt"
echo "$(date -Is) perf done" >> "$log"
wait "$rpid"; echo "$(date -Is) arm end exit=$?" >> "$log"

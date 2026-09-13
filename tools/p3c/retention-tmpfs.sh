#!/usr/bin/env bash
# Durable-2PC retention arm on tmpfs (CamusDB feature caf52e10; Kahuna.Core 1.7.6-retention.1 packed locally):
# builds the image from ~/camusdb (BUILD=1) and runs scenarios/bank-rebase-cand-p1-w128-retention-tmpfs${VARIANT:-}.yml once,
# with the tmpfs footprint / cgroup memory sampler from bank-routing-ab-tmpfs.sh so heap can be separated from data.
set -u
cd ~/camusdb-caraxes || exit 1
tag=${TAG:-r1}
log=~/camusdb-caraxes/runs/retention-tmpfs-driver-$tag.log
build_flag=""; [ "${BUILD:-0}" = "1" ] || build_flag="--skip-build"
quiet_wait() {
  local cores deadline load per
  cores=$(nproc); deadline=$(( $(date +%s) + 900 ))
  while :; do
    load=$(awk '{print $1}' /proc/loadavg); per=$(awk -v l="$load" -v c="$cores" 'BEGIN{print (l/c)}')
    awk -v p="$per" 'BEGIN{exit !(p < 0.25)}' && return 0
    [ "$(date +%s)" -ge "$deadline" ] && { echo "$(date -Is) quiet_wait gave up at load $load" >> "$log"; return 0; }
    sleep 20
  done
}
sampler() {
  echo "ts,container,data_mib,mem_current_mib,host_mem_available_mib" > "$2"
  while :; do
    avail=$(awk '/MemAvailable/{printf "%d",$2/1024}' /proc/meminfo)
    for n in 1 2 3; do
      c="$1-camus$n"; d=$(docker exec "$c" du -sm /data 2>/dev/null | awk '{print $1}'); [ -n "$d" ] || continue
      m=$(docker exec "$c" cat /sys/fs/cgroup/memory.current 2>/dev/null | awk '{printf "%d",$1/1048576}')
      echo "$(date -u +%Y-%m-%dT%H:%M:%SZ),$c,$d,$m,$avail" >> "$2"
    done
    sleep 15
  done
}
echo "$(date -Is) === START retention-tmpfs ===" >> "$log"
quiet_wait
sampler "${CLUSTER:-bankretentiontmpfs${CLUSTER_SUFFIX:-}}" ~/camusdb-caraxes/runs/tmpfs-footprint-retention-$tag.csv & spid=$!
# In-window scan-visibility probe (CamusDB feature e31cf9bc): once the leader shows resident records (load on), COUNT(*) every
# 5 s on every gateway for COUNT_PROBE_SECONDS (default 540 = most of a 10-minute window). Result summary lands in the driver log.
cpid=""
if [ "${COUNT_PROBE:-0}" = "1" ]; then
  ( until [ "$(curl -s --max-time 5 http://localhost:15095/metrics 2>/dev/null | grep -E '^kahuna_durable_tx_resident_records' | sed -E 's/\{[^}]*\}//' | awk '{s+=$2} END{print int(s)}')" -gt 50000 ] 2>/dev/null; do sleep 10; done
    echo "$(date -Is) count-probe start" >> "$log"
    PORTS="15095 15096 15097" ROWS="${COUNT_PROBE_ROWS:-2000}" DURATION="${COUNT_PROBE_SECONDS:-540}" INTERVAL=5 tools/p3c/count-probe.sh ~/camusdb-caraxes/runs/count-probe-$tag.csv >> "$log" 2>&1 ) & cpid=$!
fi
dotnet run --project Caraxes -c Release -- run --scenario "${SCENARIO:-scenarios/bank-rebase-cand-p1-w128-retention-tmpfs${VARIANT:-}.yml}" --tag "$tag" $build_flag >> "$log" 2>&1
echo "$(date -Is) === END retention-tmpfs exit=$? ===" >> "$log"
kill "$spid" 2>/dev/null; wait "$spid" 2>/dev/null; [ -n "$cpid" ] && wait "$cpid" 2>/dev/null
echo "$(date -Is) === RETENTION TMPFS COMPLETE ===" >> "$log"

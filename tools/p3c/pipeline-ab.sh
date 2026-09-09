#!/usr/bin/env bash
# Pipeline-depth A/B on tmpfs: N=1,2,4 (bank-rebase-cand-p1-w128-pipeline-n*.yml), one image (BUILD=1 builds it
# on the first arm), quiet-host gated, footprint sampler per arm. Kahuna feature fac7be26.
set -u
cd ~/camusdb-caraxes || exit 1
tag=${TAG:-n1}; log=~/camusdb-caraxes/runs/pipeline-ab-driver-$tag.log
build_flag=""; [ "${BUILD:-0}" = "1" ] || build_flag="--skip-build"
quiet_wait() { local cores deadline load per; cores=$(nproc); deadline=$(( $(date +%s) + 900 )); while :; do load=$(awk '{print $1}' /proc/loadavg); per=$(awk -v l="$load" -v c="$cores" 'BEGIN{print (l/c)}'); awk -v p="$per" 'BEGIN{exit !(p < 0.25)}' && return 0; [ "$(date +%s)" -ge "$deadline" ] && { echo "$(date -Is) quiet_wait gave up at load $load" >> "$log"; return 0; }; sleep 20; done; }
sampler() { echo "ts,container,data_mib,mem_current_mib,host_mem_available_mib" > "$2"; while :; do avail=$(awk '/MemAvailable/{printf "%d",$2/1024}' /proc/meminfo); for n in 1 2 3; do c="$1-camus$n"; d=$(docker exec "$c" du -sm /data 2>/dev/null | awk '{print $1}'); [ -n "$d" ] || continue; m=$(docker exec "$c" cat /sys/fs/cgroup/memory.current 2>/dev/null | awk '{printf "%d",$1/1048576}'); echo "$(date -u +%FT%TZ),$c,$d,$m,$avail" >> "$2"; done; sleep 15; done; }
# SCENARIO_PREFIX/CLUSTER_PREFIX select the scenario family: pipeline-n (default) or linger-l.
prefix=${SCENARIO_PREFIX:-pipeline-n}; cprefix=${CLUSTER_PREFIX:-bankpipelinen}
for n in ${ARMS:-1 2 4}; do
  echo "$(date -Is) === START $prefix$n ===" >> "$log"
  quiet_wait
  sampler "$cprefix$n" ~/camusdb-caraxes/runs/$prefix$n-footprint-$tag.csv & spid=$!
  dotnet run --project Caraxes -c Release -- run --scenario "scenarios/bank-rebase-cand-p1-w128-$prefix$n.yml" --tag "$tag" $build_flag >> "$log" 2>&1
  echo "$(date -Is) === END $prefix$n exit=$? ===" >> "$log"
  kill "$spid" 2>/dev/null; wait "$spid" 2>/dev/null
  build_flag="--skip-build"
done
echo "$(date -Is) === PIPELINE A/B COMPLETE ===" >> "$log"

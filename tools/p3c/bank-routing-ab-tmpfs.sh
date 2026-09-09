#!/usr/bin/env bash
# Routing A/B for the candidate arm with /data on tmpfs (feature 80af367a, task 6edf3c25): routing off,
# then learned, 10 minutes each, the SAME image the volume-backed pair used (caraxes/camusdb:bankrouting,
# CamusDB 3e8b8541; --skip-build on both arms so image identity is not a variable), quiet-host gated
# between runs. A side sampler records each node's /data footprint and the host's free memory every
# 15 s, because the tmpfs cap (6 GiB) and the container limit (9.5 GiB) are ceilings chosen from an
# estimate, and the evidence that they were never approached has to be on disk, not assumed.
set -u
cd ~/camusdb-caraxes || exit 1
tag=${TAG:-t1}
log=~/camusdb-caraxes/runs/bank-routing-ab-tmpfs-driver-$tag.log
# BUILD=1 builds the image on the first arm (the scenario names the tag); the second arm always skips.
build_flag=""; [ "${BUILD:-0}" = "1" ] || build_flag="--skip-build"
quiet_wait() {
  local cores deadline load per
  cores=$(nproc); deadline=$(( $(date +%s) + 900 ))
  while :; do
    load=$(awk '{print $1}' /proc/loadavg)
    per=$(awk -v l="$load" -v c="$cores" 'BEGIN{print (l/c)}')
    awk -v p="$per" 'BEGIN{exit !(p < 0.25)}' && return 0
    [ "$(date +%s)" -ge "$deadline" ] && { echo "$(date -Is) quiet_wait gave up at load $load" >> "$log"; return 0; }
    sleep 20
  done
}
sampler() { # $1 = cluster name, $2 = csv path
  echo "ts,container,data_mib,mem_current_mib,host_mem_available_mib" > "$2"
  while :; do
    avail=$(awk '/MemAvailable/{printf "%d",$2/1024}' /proc/meminfo)
    for n in 1 2 3; do
      c="$1-camus$n"
      d=$(docker exec "$c" du -sm /data 2>/dev/null | awk '{print $1}')
      [ -n "$d" ] || continue
      m=$(docker exec "$c" cat /sys/fs/cgroup/memory.current 2>/dev/null | awk '{printf "%d",$1/1048576}')
      echo "$(date -u +%Y-%m-%dT%H:%M:%SZ),$c,$d,$m,$avail" >> "$2"
    done
    sleep 15
  done
}
for arm in off learned; do
  echo "$(date -Is) === START routing-$arm-tmpfs ===" >> "$log"
  quiet_wait
  sampler "bankrebasecandp1w128rt${arm}tmpfs" ~/camusdb-caraxes/runs/tmpfs-footprint-$arm-$tag.csv &
  spid=$!
  dotnet run --project Caraxes -c Release -- run \
    --scenario "scenarios/bank-rebase-cand-p1-w128-routing-$arm-tmpfs.yml" --tag "$tag" $build_flag >> "$log" 2>&1
  build_flag="--skip-build"
  echo "$(date -Is) === END routing-$arm-tmpfs exit=$? ===" >> "$log"
  kill "$spid" 2>/dev/null; wait "$spid" 2>/dev/null
done
echo "$(date -Is) === ROUTING A/B TMPFS COMPLETE ===" >> "$log"

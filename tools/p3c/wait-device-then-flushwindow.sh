#!/usr/bin/env bash
# Waits until the NVMe's 4 KiB append+fsync p50 is back in its fast regime (< 0.6 ms) for 3 consecutive
# minutes (cap 45 min), then runs the flush-window pair on the 1.7.4 image with quiet-host waits.
set -u
cd ~/camusdb-caraxes || exit 1
log=~/camusdb-caraxes/runs/device-recovery.log
ok=0; deadline=$(( $(date +%s) + 2700 ))
while :; do
  p50=$(python3 - <<'PY'
import os,time
p='/home/kahuna/camusdb-caraxes/runs/fsync-probe.bin'; lat=[]
with open(p,'wb',buffering=0) as f:
    for i in range(30):
        f.write(os.urandom(4096)); t=time.perf_counter(); os.fsync(f.fileno()); lat.append((time.perf_counter()-t)*1000); time.sleep(0.1)
os.remove(p); lat.sort(); print(f"{lat[len(lat)//2]:.3f}")
PY
)
  echo "$(date -Is) fsync p50 ${p50} ms" >> "$log"
  if awk -v v="$p50" 'BEGIN{exit !(v < 0.6)}'; then ok=$((ok+1)); else ok=0; fi
  [ "$ok" -ge 3 ] && { echo "$(date -Is) device recovered" >> "$log"; break; }
  [ "$(date +%s)" -ge "$deadline" ] && { echo "$(date -Is) gave up waiting; launching anyway (record: device NOT recovered)" >> "$log"; break; }
  sleep 60
done
BUILD=0 ARMS="merge3 buf128" tools/p3c/waltune-trio.sh

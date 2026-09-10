#!/usr/bin/env bash
# Raft-log layout trio (CamusDB feature 6f0e0fb7, task 3): default / levelbase / universal on the 1.7.4 image,
# each via write-probe.sh (RocksDB LOG + /proc/1/io sampler), then a follower restart timing while the cluster
# is still up (teardown: false in the scenarios), then compose down -v. Output: runs/writeprobe-wt-<arm>/ and
# runs/waltune-restart.csv (arm, restart_to_ready_s, replay lines).
set -u
cd ~/camusdb-caraxes || exit 1
log=~/camusdb-caraxes/runs/waltune-trio-driver.log
csv=~/camusdb-caraxes/runs/waltune-restart.csv
[ -f "$csv" ] || echo "arm,restart_to_ready_s,restore_lines,health_probe" > "$csv"
build=${BUILD:-1}
quiet_wait() { local cores deadline load per; cores=$(nproc); deadline=$(( $(date +%s) + 900 )); while :; do load=$(awk '{print $1}' /proc/loadavg); per=$(awk -v l="$load" -v c="$cores" 'BEGIN{print (l/c)}'); awk -v p="$per" 'BEGIN{exit !(p < 0.25)}' && return 0; [ "$(date +%s)" -ge "$deadline" ] && { echo "$(date -Is) quiet_wait gave up at load $load" >> "$log"; return 0; }; sleep 20; done; }
for arm in ${ARMS:-default levelbase universal}; do
  echo "$(date -Is) === START waltune-$arm ===" >> "$log"
  quiet_wait
  TAG="wt-$arm" BUILD="$build" SCENARIO="bank-rebase-cand-p1-w128-waltune-$arm" CLUSTER="bankwaltune$arm" tools/p3c/write-probe.sh
  build=0
  # Restart timing: follower camus2 (host REST port 15096). Ready = health endpoint answers 200.
  c="bankwaltune$arm-camus2"
  if docker ps --format '{{.Names}}' | grep -q "^$c\$"; then
    t0=$(date +%s.%N); docker restart "$c" >/dev/null 2>&1
    until curl -sf -o /dev/null --max-time 2 http://localhost:15096/v1/cluster/health; do sleep 0.5; [ $(echo "$(date +%s.%N) - $t0 > 300" | bc) -eq 1 ] && break; done
    t1=$(date +%s.%N); ready=$(echo "$t1 - $t0" | bc)
    sleep 5; restore=$(docker logs "$c" 2>&1 | grep -c "Restore replay\|restore" )
    echo "$arm,$ready,$restore,$(curl -s --max-time 2 http://localhost:15096/v1/cluster/health | tr -d '\n' | cut -c1-80)" >> "$csv"
    echo "$(date -Is) restart camus2: ready after ${ready}s" >> "$log"
  else
    echo "$arm,NA,NA,cluster not up" >> "$csv"
  fi
  docker compose -f "runs/clusters/bankwaltune$arm/compose.yml" down -v >> "$log" 2>&1
  echo "$(date -Is) === END waltune-$arm ===" >> "$log"
done
echo "$(date -Is) === WALTUNE TRIO COMPLETE ===" >> "$log"

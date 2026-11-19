#!/usr/bin/env bash
# Ordered-lane A/B (Kahuna ba8ac16a): base image lane0 against candidate lane1, key-range arms only, one
# at a time in a balanced order. Fanout (cross-table two-phase commits) first, then the bank and accounts
# key-range arms. Before each arm the 1-minute load stays below 3 for a full minute and MemAvailable
# covers the arm's three node limits plus 4 GB. Driver log: runs/lane-ab-driver.log.
set -u
cd ~/camusdb-caraxes || exit 1
log=~/camusdb-caraxes/runs/lane-ab-driver.log
note() { echo "$(date -Is) $*" >> "$log"; }
quiet_wait() {
  local calm=0 load
  while [ "$calm" -lt 6 ]; do
    load=$(awk '{print $1}' /proc/loadavg)
    if awk -v l="$load" 'BEGIN{exit !(l < 3)}'; then calm=$((calm + 1)); else calm=0; fi
    sleep 10
  done
}
memory_ok() {
  local need_mb=$1 avail_mb
  avail_mb=$(awk '/MemAvailable/{print int($2/1024)}' /proc/meminfo)
  [ "$avail_mb" -ge $((need_mb + 4096)) ]
}
# scenario tag node_limit_mb
arms=(
  "lane-ab-fanout-kr-cand r1 6144" "lane-ab-fanout-kr-base r1 6144" "lane-ab-fanout-kr-base r2 6144" "lane-ab-fanout-kr-cand r2 6144"
  "lane-ab-bank-kr-base r1 4096" "lane-ab-bank-kr-cand r1 4096" "lane-ab-bank-kr-cand r2 4096"
  "lane-ab-bank-kr-base r2 4096" "lane-ab-bank-kr-base r3 4096" "lane-ab-bank-kr-cand r3 4096"
  "lane-ab-accounts-kr-base r1 6144" "lane-ab-accounts-kr-cand r1 6144" "lane-ab-accounts-kr-cand r2 6144"
  "lane-ab-accounts-kr-base r2 6144" "lane-ab-accounts-kr-base r3 6144" "lane-ab-accounts-kr-cand r3 6144"
)
note "=== START lane-ab ==="
for arm in "${arms[@]}"; do
  read -r scenario tag limit <<< "$arm"
  if [ -f "runs/scenarios/$scenario-$tag/artifacts/run/summary.json" ]; then
    note "arm $scenario $tag already has a summary; skipped"
    continue
  fi
  quiet_wait
  until memory_ok $((limit * 3)); do note "waiting for memory before $scenario $tag"; sleep 60; done
  note "arm $scenario $tag start (load $(awk '{print $1}' /proc/loadavg), MemAvailable $(awk '/MemAvailable/{print int($2/1024)}' /proc/meminfo) MB)"
  dotnet run --project Caraxes -c Release -- run --scenario "scenarios/$scenario.yml" --tag "$tag" --skip-build >> "$log" 2>&1
  note "arm $scenario $tag end exit=$?"
done
note "=== END lane-ab ==="

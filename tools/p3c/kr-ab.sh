#!/usr/bin/env bash
# Key-range vs hash routing A/B: 12 arms on one image, balanced order, one at a time.
#   ROUND=p KRAB_IMAGE=caraxes/camusdb:krab2 tools/p3c/kr-ab.sh   (run tags <ROUND>1..3; the scenarios name the image)
# Before each arm: the image build has finished, the 1-minute load stays below 3 for a full minute, and
# MemAvailable covers the arm's three node limits plus 4 GB. Driver log: runs/kr-ab-driver.log.
set -u
cd ~/camusdb-caraxes || exit 1
round=${ROUND:-r}
image=${KRAB_IMAGE:-caraxes/camusdb:krab}
log=~/camusdb-caraxes/runs/kr-ab-driver-$round.log
buildlog=~/camusdb-caraxes/runs/${image##*:}-build.log
note() { echo "$(date -Is) $*" >> "$log"; }

wait_build() {
  until grep -q "^BUILD EXIT" "$buildlog" 2>/dev/null; do sleep 30; done
  grep -q "^BUILD EXIT 0$" "$buildlog" || { note "image build failed; stopping"; exit 1; }
}
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
  "kr-ab-bank-hash ${round}1 4096" "kr-ab-bank-kr ${round}1 4096" "kr-ab-bank-kr ${round}2 4096"
  "kr-ab-bank-hash ${round}2 4096" "kr-ab-bank-hash ${round}3 4096" "kr-ab-bank-kr ${round}3 4096"
  "kr-ab-accounts-hash ${round}1 6144" "kr-ab-accounts-kr ${round}1 6144" "kr-ab-accounts-kr ${round}2 6144"
  "kr-ab-accounts-hash ${round}2 6144" "kr-ab-accounts-hash ${round}3 6144" "kr-ab-accounts-kr ${round}3 6144"
)

note "=== START kr-ab round $round on $image ==="
wait_build
note "image ready"
for arm in "${arms[@]}"; do
  read -r scenario tag limit <<< "$arm"
  if [ -d "runs/scenarios/$scenario-$tag" ] && [ -f "runs/scenarios/$scenario-$tag/artifacts/run/summary.json" ]; then
    note "arm $scenario $tag already has a summary; skipped"
    continue
  fi
  quiet_wait
  until memory_ok $((limit * 3)); do note "waiting for memory before $scenario $tag"; sleep 60; done
  note "arm $scenario $tag start (load $(awk '{print $1}' /proc/loadavg), MemAvailable $(awk '/MemAvailable/{print int($2/1024)}' /proc/meminfo) MB)"
  dotnet run --project Caraxes -c Release -- run --scenario "scenarios/$scenario.yml" --tag "$tag" --skip-build >> "$log" 2>&1
  note "arm $scenario $tag end exit=$?"
done
note "=== END kr-ab ==="

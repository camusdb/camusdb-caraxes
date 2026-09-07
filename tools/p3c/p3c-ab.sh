#!/usr/bin/env bash
# Interleaved A/B driver for a Phase 3 close-out cell (CamusDB feature cfe716da).
#
# Usage: p3c-ab.sh <cell> <reps> <arm> [arm ...]
#   e.g. p3c-ab.sh grp-accounts 3 cand onep
#        p3c-ab.sh fanout 3 base cand onep
#
# Runs the arms of one replicate back to back, so a comparison survives this host's habit of moving its
# durability path between runs; waits for the host to go quiet first, so the harness's require_quiet_host
# gate grades an ambient sample rather than the tail of the previous run.
set -u
cell="$1"; reps="$2"; shift 2
arms=("$@")
cd ~/camusdb-caraxes || exit 1
log=~/camusdb-caraxes/runs/p3c-$cell-driver.log

quiet_wait() {
  local cores deadline load per
  cores=$(nproc); deadline=$(( $(date +%s) + 1800 ))
  while :; do
    load=$(awk '{print $1}' /proc/loadavg)
    per=$(awk -v l="$load" -v c="$cores" 'BEGIN{print (l/c)}')
    awk -v p="$per" 'BEGIN{exit !(p < 0.25)}' && return 0
    if [ "$(date +%s)" -ge "$deadline" ]; then
      echo "$(date -Is) quiet_wait gave up at load $load over $cores cores" >> "$log"; return 0
    fi
    sleep 30
  done
}

for rep in $(seq 1 "$reps"); do
  for arm in "${arms[@]}"; do
    echo "$(date -Is) === START $cell $arm r$rep ===" >> "$log"
    quiet_wait
    dotnet run --project Caraxes -c Release -- run \
      --scenario "scenarios/p3c-$cell-$arm.yml" --tag "r$rep" --skip-build >> "$log" 2>&1
    echo "$(date -Is) === END $cell $arm r$rep exit=$? ===" >> "$log"
  done
done
echo "$(date -Is) === CAMPAIGN COMPLETE: $cell ===" >> "$log"

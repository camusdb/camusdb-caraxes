#!/usr/bin/env bash
# Interleaved A/B driver for the client-routing campaign (CamusDB `e89bc8c4`).
#
# Usage: tools/p3c/cr-ab.sh <reps> <cell> [cell ...]
#   e.g. tools/p3c/cr-ab.sh 3 accounts fanout
#
# Separate from p3c-ab.sh on purpose. That script's semantics — always --skip-build, fixed arm order —
# are what several recorded p3c results were produced under, and quietly changing them would make those
# records mean something slightly different than when they were written.
#
# Two differences from p3c-ab.sh, both deliberate:
#
#   1. The first run of a cell BUILDS its image; the rest reuse it. Both arms of this campaign name the
#      same image because routing is a client-side behaviour — the server binary must not differ between
#      them — so building once is what guarantees that.
#   2. The arm order ALTERNATES between replicates (off,learned then learned,off then off,learned). The
#      bank re-baseline (`80af367a`) found this host's between-run variance is not a monotonic function
#      of wall-clock time, so a fixed arm order gives one arm the same slot in every replicate. Arms are
#      still compared only within a replicate; alternating just stops a slot effect from accumulating in
#      one direction across all three.
set -u
reps="$1"; shift
cells=("$@")
cd ~/camusdb-caraxes || exit 1
log=~/camusdb-caraxes/runs/cr-driver.log

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

build_flag=""
for cell in "${cells[@]}"; do
  for rep in $(seq 1 "$reps"); do
    if [ $(( rep % 2 )) -eq 1 ]; then arms=(off learned); else arms=(learned off); fi
    for arm in "${arms[@]}"; do
      echo "$(date -Is) === START cr-$cell-$arm r$rep ===" >> "$log"
      quiet_wait
      # shellcheck disable=SC2086
      dotnet run --project Caraxes -c Release -- run \
        --scenario "scenarios/cr-$cell-$arm.yml" --tag "r$rep" $build_flag >> "$log" 2>&1
      echo "$(date -Is) === END cr-$cell-$arm r$rep exit=$? ===" >> "$log"
      build_flag="--skip-build"
    done
  done
done
echo "$(date -Is) === CLIENT ROUTING CAMPAIGN COMPLETE ===" >> "$log"

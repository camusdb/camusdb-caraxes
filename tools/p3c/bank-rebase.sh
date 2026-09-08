#!/usr/bin/env bash
# ABBA driver for the sustained `bank` re-baseline on the 1.6.6 stack.
#
# Runs: base, cand, cand, base — four 45-minute soaks, adjacent, quiet-host gated between each.
#
# The ordering is the experiment's drift control, not a formality. Across six 45-minute p1/w128 arms
# this host's wall-clock start time predicted throughput at Pearson -0.963, and two byte-identical
# server binaries differed by 1.36x (see the `bank-soak-k160-p1-w128` header). A single base-then-cand
# pair therefore measures the hour as much as the stack. ABBA cancels a monotonic trend exactly:
# pair 1 is (base, cand) and pair 2 is (cand, base), so whatever the host is drifting toward affects
# the two arms in opposite directions and averages out. If the two pairs disagree in DIRECTION, the
# drift is not monotonic, the ratio is not reportable, and that disagreement is the finding.
#
# Usage: tools/p3c/bank-rebase.sh
set -u
cd ~/camusdb-caraxes || exit 1
log=~/camusdb-caraxes/runs/bank-rebase-driver.log

# base=A cand=B, run A B B A
seq=("base-p3-w32:p1" "cand-p1-w128:p1" "cand-p1-w128:p2" "base-p3-w32:p2")

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

# The first run builds the image; the remaining three reuse it. Both arms name the same image, so all
# four soaks run byte-identical server binaries and the arms cannot differ by a rebuild.
build_flag=""
for entry in "${seq[@]}"; do
  arm="${entry%%:*}"; tag="${entry##*:}"
  echo "$(date -Is) === START bank-rebase-$arm $tag ===" >> "$log"
  quiet_wait
  # shellcheck disable=SC2086
  dotnet run --project Caraxes -c Release -- run \
    --scenario "scenarios/bank-rebase-$arm.yml" --tag "$tag" $build_flag >> "$log" 2>&1
  echo "$(date -Is) === END bank-rebase-$arm $tag exit=$? ===" >> "$log"
  build_flag="--skip-build"
done
echo "$(date -Is) === BANK RE-BASELINE COMPLETE ===" >> "$log"

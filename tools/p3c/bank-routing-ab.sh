#!/usr/bin/env bash
# Routing A/B for the candidate arm on the deferred-START / session-anchoring build: routing off, then
# learned, 10 minutes each, one image built once, quiet-host gated between runs (feature 80af367a).
set -u
cd ~/camusdb-caraxes || exit 1
log=~/camusdb-caraxes/runs/bank-routing-ab-driver.log
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
build_flag=""
for arm in off learned; do
  echo "$(date -Is) === START routing-$arm ===" >> "$log"
  quiet_wait
  # shellcheck disable=SC2086
  dotnet run --project Caraxes -c Release -- run \
    --scenario "scenarios/bank-rebase-cand-p1-w128-routing-$arm.yml" --tag a1 $build_flag >> "$log" 2>&1
  echo "$(date -Is) === END routing-$arm exit=$? ===" >> "$log"
  build_flag="--skip-build"
done
echo "$(date -Is) === ROUTING A/B COMPLETE ===" >> "$log"

#!/usr/bin/env bash
# Two identical steady-regime runs of the candidate arm (bank-rebase-cand-p1-w128-precond.yml), quiet-host
# gated between them. Success = both pass `p3c regime` and their ops/s agree within a few percent.
set -u
cd ~/camusdb-caraxes || exit 1
log=~/camusdb-caraxes/runs/precond-pair-driver.log
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
for tag in q1 q2 q3; do
  echo "$(date -Is) === START precond $tag ===" >> "$log"
  quiet_wait
  dotnet run --project Caraxes -c Release -- run --scenario scenarios/bank-rebase-cand-p1-w128-precond.yml --tag "$tag" --skip-build >> "$log" 2>&1
  echo "$(date -Is) === END precond $tag exit=$? ===" >> "$log"
done
echo "$(date -Is) === PRECOND PAIR COMPLETE ===" >> "$log"

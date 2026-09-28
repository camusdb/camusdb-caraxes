#!/usr/bin/env bash
# A6 pair on the tmpfs learned bank shape (Kommander fc4dabe7 task 3; CamusDB plan 84b26503 row A6). ONE image
# caraxes/camusdb:banka6 (CamusDB 8ebedede + local packs Kommander 1.9.1-round.1 -> Kahuna.Core/Shared 1.10.2-round.1),
# built beforehand from ~/camusdb, both arms --skip-build. Compare with ss1 on bankrel1101.
#
#   arm 1  scenarios/bank-a6-tmpfs-learned-off.yml     histogram off (control); the unperturbed schedstat sampler runs over it
#   arm 2  scenarios/bank-a6-tmpfs-learned-stages.yml  KOMMANDER_ROUND_STAGES=1 (the per-stage round histogram)
#
#   TAG=a6s1 tools/p3c/a6-pair.sh
#
# Output: runs/scenarios/bank-a6-tmpfs-learned-{off,stages}-<tag>/; arm 1 also gets diagnostics/schedstat-threads.csv.
# Driver log: runs/a6-pair-driver-<tag>.log. Guards: waits for any CamusDB test run (the test host process) and any
# image build to finish before each arm, and for a quiet host.
set -u
cd ~/camusdb-caraxes || exit 1
tag=${TAG:-a6s1}
log=~/camusdb-caraxes/runs/a6-pair-driver-$tag.log
note() { echo "$(date -Is) $*" >> "$log"; }
quiet_wait() {
  local cores deadline load per
  cores=$(nproc); deadline=$(( $(date +%s) + 900 ))
  while :; do
    load=$(awk '{print $1}' /proc/loadavg); per=$(awk -v l="$load" -v c="$cores" 'BEGIN{print (l/c)}')
    awk -v p="$per" 'BEGIN{exit !(p < 0.25)}' && return 0
    [ "$(date +%s)" -ge "$deadline" ] && { note "quiet_wait gave up at load $load"; return 0; }
    sleep 20
  done
}
busy_wait() {
  while pgrep -f "[d]otnet exec .*CamusDB\.Tests" > /dev/null; do sleep 15; done
  while pgrep -f "[d]ocker build" > /dev/null; do sleep 15; done
}

note "=== START a6 pair ==="

# ---- arm 1: control + sampler --------------------------------------------------------------------------------------------
scenario=bank-a6-tmpfs-learned-off
cluster=banka6off
out=~/camusdb-caraxes/runs/scenarios/$scenario-$tag/diagnostics
busy_wait; quiet_wait
note "arm 1 ($scenario) start (load $(awk '{print $1}' /proc/loadavg))"
dotnet run --project Caraxes -c Release -- run --scenario "scenarios/$scenario.yml" --tag "$tag" --skip-build >> "$log" 2>&1 &
rpid=$!
until grep -q '==> running workload' "$log"; do
  kill -0 "$rpid" 2>/dev/null || { note "arm 1 exited before the workload started"; exit 1; }
  sleep 2
done
note "workload started (warm-up 6m begins)"
mkdir -p "$out"
tools/traceattr/schedstat-sample.sh "$out/schedstat-threads.csv" "$cluster" 1080 &
spid=$!
wait "$rpid"
note "arm 1 end exit=$?"
wait "$spid" 2>/dev/null

# ---- arm 2: stages histogram ----------------------------------------------------------------------------------------------
scenario=bank-a6-tmpfs-learned-stages
busy_wait; quiet_wait
note "arm 2 ($scenario) start (load $(awk '{print $1}' /proc/loadavg))"
dotnet run --project Caraxes -c Release -- run --scenario "scenarios/$scenario.yml" --tag "$tag" --skip-build >> "$log" 2>&1
note "arm 2 end exit=$?"
note "=== END a6 pair ==="

#!/usr/bin/env bash
# Run scenarios one after another on an idle host, all --skip-build (the image exists), one driver log per tag.
#   SCENS="bank-a6-tmpfs-learned-hold1 bank-a6-tmpfs-learned-hold0" TAG=hs1 tools/p3c/run-arms.sh
# Guards before each arm: no CamusDB test run (`dotnet exec … CamusDB.Tests…`), no image build, host load < 0.25 per core
# (or 15 minutes of waiting). Output: runs/scenarios/<scenario>-<tag>/; driver log runs/run-arms-driver-<tag>.log.
set -u
cd ~/camusdb-caraxes || exit 1
tag=${TAG:?set TAG}
scens=${SCENS:?set SCENS to a space-separated list of scenario names}
log=~/camusdb-caraxes/runs/run-arms-driver-$tag.log
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
note "=== START arms: $scens ==="
for scenario in $scens; do
  busy_wait; quiet_wait
  note "arm $scenario start (load $(awk '{print $1}' /proc/loadavg))"
  dotnet run --project Caraxes -c Release -- run --scenario "scenarios/$scenario.yml" --tag "$tag" --skip-build >> "$log" 2>&1
  note "arm $scenario end exit=$?"
done
note "=== END arms ==="

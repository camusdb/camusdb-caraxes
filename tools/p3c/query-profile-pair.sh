#!/usr/bin/env bash
# Point-read attribution pair: the tmpfs learned bank shape on ONE image,
# profile off (control: the unperturbed query mean) then profile on (CAMUS_QUERY_STAGE_PROFILE=1). The image
# caraxes/camusdb:bankqprof1 is built beforehand from ~/camusdb, so both arms run --skip-build.
#
#   TAG=qp1 [SCEN=bank-qprof-tmpfs-learned] tools/p3c/query-profile-pair.sh
#   tools/p3c/query-profile.py runs/scenarios/bank-qprof-tmpfs-learned-on-qp1
set -u
cd ~/camusdb-caraxes || exit 1
tag=${TAG:-qp1}
log=~/camusdb-caraxes/runs/query-profile-driver-$tag.log
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
note "=== START query-profile pair ==="
while pgrep -f "[t]esthost.*CamusDB.Tests.dll" > /dev/null; do sleep 15; done
for arm in off on; do
  quiet_wait
  note "arm $arm start (load $(awk '{print $1}' /proc/loadavg))"
  dotnet run --project Caraxes -c Release -- run --scenario "scenarios/${SCEN:-bank-qprof-tmpfs-learned}-$arm.yml" --tag "$tag" --skip-build >> "$log" 2>&1
  note "arm $arm end exit=$?"
done
note "=== END query-profile pair ==="

#!/usr/bin/env bash
# Kommander round-cost acceptance campaign (Kommander feature fc4dabe7 task 4). Two images from ONE CamusDB worktree
# (8ebedede + Kahuna.Core 1.10.1): caraxes/camusdb:bankr3base (Kommander 1.9.0 as shipped) and caraxes/camusdb:bankr3cand
# (Kommander 1.9.1-round.2, a direct reference to the local pack). Built beforehand; every arm runs --skip-build.
#
#   1-4  scenarios/bank-r3-tmpfs-learned-45-{base,cand}.yml   interleaved base, cand, base, cand (45 min each, tmpfs)
#   5    scenarios/bank-r3-tmpfs-learned-stages-cand.yml      KOMMANDER_ROUND_STAGES=1 on the candidate (compare with ss1)
#   6    scenarios/bank-fault-soak-nvme-r3cand.yml            the two-hour fault soak on the candidate (compare with fs13)
#
#   TAG=r3a ARMS="1 2 3 4 5 6" tools/p3c/r3-campaign.sh
#
# Output: runs/scenarios/<scenario>-<tag><n>/ per arm. Driver log: runs/r3-campaign-driver-<tag>.log. Guards before each
# arm: no CamusDB test run, no image build, and a quiet host (load per core < 0.25, give up after 15 min and note it).
set -u
cd ~/camusdb-caraxes || exit 1
tag=${TAG:-r3a}
arms=${ARMS:-1 2 3 4 5 6}
log=~/camusdb-caraxes/runs/r3-campaign-driver-$tag.log
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
scenario_of() {
  case "$1" in
    1|3) echo bank-r3-tmpfs-learned-45-base ;;
    2|4) echo bank-r3-tmpfs-learned-45-cand ;;
    5)   echo bank-r3-tmpfs-learned-stages-cand ;;
    6)   echo bank-fault-soak-nvme-r3cand ;;
  esac
}

note "=== START r3 campaign (arms: $arms) ==="
for n in $arms; do
  scenario=$(scenario_of "$n")
  busy_wait; quiet_wait
  note "arm $n ($scenario) start (load $(awk '{print $1}' /proc/loadavg))"
  dotnet run --project Caraxes -c Release -- run --scenario "scenarios/$scenario.yml" --tag "$tag$n" --skip-build >> "$log" 2>&1
  note "arm $n end exit=$?"
done
note "=== END r3 campaign ==="

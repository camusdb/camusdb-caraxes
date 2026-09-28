#!/usr/bin/env bash
# Per-thread schedstat snapshot of every node every 10 s: unix_s,node,tid,comm,cpu_ns,runq_ns,slices
out=$1; cluster=$2; secs=${3:-600}
echo "unix_s,node,tid,comm,cpu_ns,runq_ns,slices" > "$out"
end=$(( $(date +%s) + secs ))
while [ "$(date +%s)" -lt "$end" ]; do
  now=$(date +%s)
  for n in 1 2 3; do
    p=$(docker inspect -f '{{.State.Pid}}' "$cluster-camus$n" 2>/dev/null) || continue
    for t in /proc/$p/task/*; do
      s=$(cat $t/schedstat 2>/dev/null) || continue
      printf '%s,camus%s,%s,%s,%s\n' "$now" "$n" "${t##*/}" "$(tr ',' ' ' < $t/comm 2>/dev/null)" "${s// /,}"
    done >> "$out"
  done
  el=$(( $(date +%s) - now )); [ $el -lt 10 ] && sleep $(( 10 - el ))
done

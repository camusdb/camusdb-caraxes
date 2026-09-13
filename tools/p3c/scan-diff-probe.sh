#!/usr/bin/env bash
# Which rows does a concurrent scan miss? Repeatedly SELECT id FROM <table> on one gateway, diff against the full id set
# (BASELINE file: one id per line, taken idle), and for every missing id issue an immediate point read on the same gateway.
# A point read that returns the row proves the scan skipped a live row. Usage:
#   PORT=15095 ROUNDS=20 tools/p3c/scan-diff-probe.sh <baseline-ids.txt> <out.txt>
set -u
base=${1:?baseline ids}; out=${2:?out}; port=${PORT:-15095}; rounds=${ROUNDS:-20}; db=${DB:-caraxes}; table=${TABLE:-workload_accounts}
: > "$out"
for i in $(seq 1 "$rounds"); do
  ts=$(date -u +%H:%M:%S.%3N)
  resp=$(curl -s --max-time 60 -X POST "http://localhost:$port/execute-sql-query" -H 'Content-Type: application/json' -d "{\"databaseName\":\"$db\",\"sql\":\"SELECT id FROM $table\"}")
  ms=$(echo "$resp" | grep -oE '"serverTimeMs":[0-9.]+' | cut -d: -f2)
  echo "$resp" | python3 -c "import sys,json
d=json.load(sys.stdin); ids=[r[0] for r in d.get('rows',[])]; print('\n'.join(ids))" 2>/dev/null | sort > /tmp/scan-ids.$$
  n=$(wc -l < /tmp/scan-ids.$$)
  missing=$(comm -23 "$base" /tmp/scan-ids.$$)
  extra=$(comm -13 "$base" /tmp/scan-ids.$$ | wc -l)
  echo "round $i $ts rows=$n ms=$ms missing=$(echo "$missing" | grep -c .) extra=$extra" >> "$out"
  for id in $missing; do
    pr=$(curl -s --max-time 20 -X POST "http://localhost:$port/execute-sql-query" -H 'Content-Type: application/json' -d "{\"databaseName\":\"$db\",\"sql\":\"SELECT id, version, balance FROM $table WHERE id = '$id'\"}")
    echo "   missing $id -> point read: $(echo "$pr" | grep -oE '"rows":\[\[[^]]*\]\]|"code":"[A-Z0-9]+"' | head -1)" >> "$out"
  done
done
rm -f /tmp/scan-ids.$$
grep -c "point read: \"rows\":\[\[\"" "$out" | xargs -I{} echo "missing rows that a point read DID return: {}" >> "$out"
cat "$out" | head -60

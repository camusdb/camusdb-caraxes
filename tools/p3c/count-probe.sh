#!/usr/bin/env bash
# In-window scan-visibility probe (CamusDB feature e31cf9bc): runs SELECT COUNT(*) FROM <table> against every gateway's REST
# endpoint every INTERVAL seconds for DURATION seconds and records the rows returned and server time. A read_committed scan
# must return the row count every time, load or no load. Usage:
#   PORTS="15095 15096 15097" ROWS=2000 DURATION=300 INTERVAL=5 tools/p3c/count-probe.sh <out.csv>
set -u
out=${1:?out csv}; ports=${PORTS:-15095 15096 15097}; rows=${ROWS:-2000}; dur=${DURATION:-300}; iv=${INTERVAL:-5}
db=${DB:-caraxes}; table=${TABLE:-workload_accounts}
echo "ts,port,rows,server_ms,status,code" > "$out"
end=$(( $(date +%s) + dur ))
while [ "$(date +%s)" -lt "$end" ]; do
  for p in $ports; do
    resp=$(curl -s --max-time 60 -X POST "http://localhost:$p/execute-sql-query" -H 'Content-Type: application/json' \
      -d "{\"databaseName\":\"$db\",\"sql\":\"SELECT COUNT(*) FROM $table\"}")
    r=$(echo "$resp" | grep -oE '"rows":\[\[[0-9]+\]\]' | grep -oE '[0-9]+' | head -1)
    ms=$(echo "$resp" | grep -oE '"serverTimeMs":[0-9.]+' | cut -d: -f2)
    st=$(echo "$resp" | grep -oE '"status":"[a-z]+"' | cut -d'"' -f4)
    code=$(echo "$resp" | grep -oE '"code":"[A-Z0-9]+"' | cut -d'"' -f4)
    echo "$(date -u +%Y-%m-%dT%H:%M:%SZ),$p,${r:-},${ms:-},${st:-none},${code:-}" >> "$out"
  done
  sleep "$iv"
done
python3 - "$out" "$rows" <<'PY'
import csv,sys
rows=list(csv.DictReader(open(sys.argv[1]))); n=int(sys.argv[2])
ok=[r for r in rows if r['rows']==str(n)]; short=[r for r in rows if r['rows'] and r['rows']!=str(n)]; err=[r for r in rows if not r['rows']]
print(f"probes {len(rows)}: exact {len(ok)}, SHORT {len(short)}, errors {len(err)}")
for r in short[:12]: print("  short:", r['ts'], r['port'], r['rows'], r['server_ms'])
for r in err[:4]: print("  error:", r['ts'], r['port'], r['status'], r['code'])
PY

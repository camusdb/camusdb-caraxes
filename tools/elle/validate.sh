#!/usr/bin/env bash
# Checks that the elle-cli jar finds what it must find and passes what it must pass, on small histories
# whose verdicts are known by construction (tools/elle/fixtures). The harness runs this before it
# trusts a checker build: a checker that calls a lost update valid, or a clean history invalid, would
# turn every green run into no evidence at all, and nothing in a passing scenario would say so.
#
# Each fixture is checked under the model and consistency level it was written for, and the verdict
# must match. The history lines are the shape CamusDB.Workload writes (one EDN map per line).
#
#   rw-valid                 rw-register  serializable    true   reads see earlier writes, nothing cyclic
#   rw-g1a-aborted-read      rw-register  read-committed  false  a read observed a write the server refused (G1a)
#   rw-g1b-intermediate-read rw-register  read-committed  false  a read observed a value a transaction overwrote before commit (G1b)
#   rw-lost-update-g2        rw-register  serializable    false  two writers each read nil and wrote; both committed (G2-item)
#   rw-lost-update-g2        rw-register  read-committed  true   read committed allows that cycle
#   pred-valid               rw-register  serializable    true   group reads as nil-reads of absent keys, inserts observed in order
#   pred-write-skew          rw-register  serializable    false  two transactions each saw the other's key absent and inserted (G2-item)
#   pred-write-skew          rw-register  read-committed  true   allowed at read committed
#   pred-phantom             rw-register  serializable    false  a transaction saw a key absent, then present (G-single)
#   append-valid             list-append  serializable    true
#   append-g1c               list-append  read-committed  false  two transactions each read the other's append (G1c)
#   append-incompatible-order list-append serializable    false  one read saw [2], another [1 2]: no single order of the list fits both
#
# An acknowledged append that no read ever observes is NOT an Elle anomaly (nothing orders it), which
# is why the workload also judges each list's final state against its ledger after the run.
#
# Needs Docker (the JDK image the harness uses) or a local java 21. Exit 1 on any mismatch.
set -euo pipefail

DIR="$(cd "$(dirname "$0")" && pwd)"
JAR="${ELLE_JAR:-$DIR/elle-cli-0.1.11-elle0.2.8-standalone.jar}"
IMAGE="${ELLE_IMAGE:-eclipse-temurin:21-jdk}"
FIXTURES="$DIR/fixtures"

if [[ ! -f "$JAR" ]]; then
  echo "checker jar not found at $JAR; run tools/elle/build.sh" >&2
  exit 1
fi

run_elle() {
  # $1 model, $2 consistency models, $3 history file; prints elle-cli's JSON on stdout.
  if command -v java >/dev/null 2>&1 && java -version 2>&1 | grep -q 'version "21'; then
    java -jar "$JAR" --model "$1" --output json --consistency-models "$2" --max-plot-bytes 0 "$3" 2>/dev/null || true
  else
    docker run --rm \
      -v "$JAR:/elle/elle-cli.jar:ro" -v "$FIXTURES:/work:ro" \
      --entrypoint java "$IMAGE" \
      -jar /elle/elle-cli.jar --model "$1" --output json --consistency-models "$2" --max-plot-bytes 0 "/work/$(basename "$3")" 2>/dev/null || true
  fi
}

verdict_of() {
  # The first JSON object on stdout; "valid?" is true, false or "unknown".
  python3 -I -c '
import json, sys
text = sys.stdin.read()
start = text.find("{")
if start < 0:
    print("none"); sys.exit(0)
try:
    doc = json.loads(text[start:])
except json.JSONDecodeError:
    print("unparsed"); sys.exit(0)
v = doc.get("valid?")
anomalies = ",".join(doc.get("anomaly-types", []) or [])
print(("true" if v is True else "false" if v is False else str(v)) + (" " + anomalies if anomalies else ""))
'
}

failures=0
check() {
  local fixture="$1" model="$2" levels="$3" expected="$4"
  local output verdict
  output="$(run_elle "$model" "$levels" "$FIXTURES/$fixture.edn")"
  verdict="$(printf '%s' "$output" | verdict_of)"
  local got="${verdict%% *}"
  local anomalies="${verdict#"$got"}"
  if [[ "$got" == "$expected" ]]; then
    printf '  ok    %-26s %-12s %-15s %-5s%s\n' "$fixture" "$model" "$levels" "$got" "$anomalies"
  else
    printf '  WRONG %-26s %-12s %-15s expected %s, got %s%s\n' "$fixture" "$model" "$levels" "$expected" "$got" "$anomalies"
    failures=$((failures + 1))
  fi
}

echo "validating $(basename "$JAR")"
check rw-valid                 rw-register serializable   true
check rw-g1a-aborted-read      rw-register read-committed false
check rw-g1b-intermediate-read rw-register read-committed false
check rw-lost-update-g2        rw-register serializable   false
check rw-lost-update-g2        rw-register read-committed true
check pred-valid               rw-register serializable   true
check pred-write-skew          rw-register serializable   false
check pred-write-skew          rw-register read-committed true
check pred-phantom             rw-register serializable   false
check append-valid             list-append serializable   true
check append-g1c               list-append read-committed false
check append-incompatible-order list-append serializable   false

if [[ "$failures" -ne 0 ]]; then
  echo "$failures fixture verdict(s) wrong; this checker build must not grade a run" >&2
  exit 1
fi
echo "every fixture verdict matched"

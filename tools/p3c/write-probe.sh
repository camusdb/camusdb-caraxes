#!/usr/bin/env bash
# Write-attribution probe driver: runs bank-rebase-cand-p1-w128-writeprobe.yml once and samples each node's
# I/O totals, data footprint and RocksDB LOG files into runs/writeprobe-<tag>/ (feature cfe716da part 2, step 1).
set -u
cd ~/camusdb-caraxes || exit 1
tag=${TAG:-w1}; out=~/camusdb-caraxes/runs/writeprobe-$tag; mkdir -p "$out"
build_flag=""; [ "${BUILD:-0}" = "1" ] || build_flag="--skip-build"
log=$out/driver.log
sampler() {
  echo "ts,container,write_bytes,cancelled_write_bytes,kv_mib,wal_mib,logs_mib" > "$out/io.csv"
  while :; do
    for n in 1 2 3; do
      c="bankwriteprobe-camus$n"
      io=$(docker exec "$c" cat /proc/1/io 2>/dev/null) || continue
      wb=$(echo "$io" | awk '/^write_bytes/{print $2}'); cwb=$(echo "$io" | awk '/^cancelled_write_bytes/{print $2}')
      d=$(docker exec "$c" sh -c 'du -sm /data/kv /data/wal 2>/dev/null | cut -f1 | tr "\n" ","; du -sm /data --exclude=/data/kv --exclude=/data/wal 2>/dev/null | cut -f1')
      echo "$(date -u +%Y-%m-%dT%H:%M:%SZ),$c,$wb,$cwb,$d" >> "$out/io.csv"
      for db in kv wal; do docker exec "$c" sh -c "cat /data/$db/*/LOG 2>/dev/null" > "$out/rocksdb-LOG-$db-camus$n.txt.tmp" 2>/dev/null && mv "$out/rocksdb-LOG-$db-camus$n.txt.tmp" "$out/rocksdb-LOG-$db-camus$n.txt"; done
    done
    sleep 10
  done
}
echo "$(date -Is) === START writeprobe ===" >> "$log"
sampler & spid=$!
dotnet run --project Caraxes -c Release -- run --scenario scenarios/bank-rebase-cand-p1-w128-writeprobe.yml --tag "$tag" $build_flag >> "$log" 2>&1
echo "$(date -Is) === END writeprobe exit=$? ===" >> "$log"
kill "$spid" 2>/dev/null; wait "$spid" 2>/dev/null
echo "$(date -Is) === WRITEPROBE COMPLETE ===" >> "$log"

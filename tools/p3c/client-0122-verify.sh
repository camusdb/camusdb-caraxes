#!/usr/bin/env bash
# Verifies CamusDB.Client 0.12.2 (coalescing fix) end to end with the DEFAULT client on the 1.7.3 image:
# lowc-w8h0 (expect ~5 ms write p50, ~2,450 ops/s like nocoal-w8h0) then hold-h2 (expect ~7,300 like nocoal-w128h2).
set -u
cd ~/camusdb-caraxes || exit 1
TAG=v1 BUILD=0 ARMS="w8h0" SCENARIO_PREFIX=lowc- CLUSTER_PREFIX=banklowc tools/p3c/pipeline-ab.sh
TAG=v1 BUILD=0 ARMS="2" SCENARIO_PREFIX=hold-h CLUSTER_PREFIX=bankholdh tools/p3c/pipeline-ab.sh
echo "$(date -Is) === CLIENT 0.12.2 VERIFY COMPLETE ===" >> ~/camusdb-caraxes/runs/client-0122-verify.log

#!/usr/bin/env bash
# Waits for the low-concurrency quartet (tag c1) to finish, then runs the client-coalescing-off trio on the
# same 1.7.2 image: nocoal-w8h0 (server floor), nocoal-w128h0, nocoal-w128h2 (Kahuna feature fac7be26).
set -u
cd ~/camusdb-caraxes || exit 1
until grep -q "PIPELINE A/B COMPLETE" runs/pipeline-ab-driver-c1.log 2>/dev/null; do sleep 15; done
TAG=d0 BUILD=0 ARMS="w8h0 w128h0 w128h2" SCENARIO_PREFIX=nocoal- CLUSTER_PREFIX=banknocoal tools/p3c/pipeline-ab.sh

# Layers the clock skew shim (skew.c) on top of a built CamusDB node image. Caraxes builds this only
# for a cluster with clock_skew: true, tagging the result <node image>-clockskew, so the CamusDB image
# itself carries no chaos tooling and any CamusDB ref can run a clock-skew scenario.
#
#   docker build --build-arg BASE_IMAGE=caraxes/camusdb:append -t caraxes/camusdb:append-clockskew tools/clockskew
#
# The compiler stage is the .NET SDK image because the CamusDB build already pulled it and it shares
# the runtime image's distribution (and so its glibc). The shim only links libc and libdl, so a newer
# glibc in the runtime image also loads it.
ARG BASE_IMAGE

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
RUN apt-get update \
  && apt-get install -y --no-install-recommends gcc libc6-dev \
  && rm -rf /var/lib/apt/lists/*
COPY skew.c /src/skew.c
# -Wno-nonnull-compare: glibc declares gettimeofday's tv nonnull, but a NULL tv is legal to pass.
RUN gcc -O2 -Wall -Wextra -Werror -Wno-nonnull-compare -shared -fPIC \
      -o /libcaraxes-skew.so /src/skew.c -ldl

FROM ${BASE_IMAGE}
COPY --from=build /libcaraxes-skew.so /usr/local/lib/libcaraxes-skew.so

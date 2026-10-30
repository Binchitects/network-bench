# Benchmark clients: membench (zero-copy, reports client vs server bottleneck) plus curl, aria2c, lftp, smbclient, iperf3.
# Build from the repo root:  docker build -t memserve-clients -f docker/clients/Dockerfile .
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG TARGETARCH
WORKDIR /src
COPY src/MemServe/ MemServe/
COPY src/MemBench/ MemBench/
RUN RID=linux-musl-$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64) && \
    dotnet publish MemBench/MemBench.csproj -c Release -r $RID --self-contained \
      -p:PublishSingleFile=true -p:DebugType=none -o /out

FROM alpine:3.22
RUN apk add --no-cache bash curl aria2 lftp samba-client cifs-utils iperf3 coreutils libstdc++ libgcc
COPY --from=build /out/membench /usr/local/bin/membench
CMD ["bash"]

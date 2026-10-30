# memserve: in-memory HTTP/HTTPS + FTP/FTPS benchmark server (zero-copy without TLS, kernel TLS with it)
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG TARGETARCH
WORKDIR /src
COPY src/MemServe/MemServe.csproj ./
RUN RID=linux-musl-$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64) && \
    echo $RID > /rid && dotnet restore -r $RID
COPY src/MemServe/ ./
RUN dotnet publish -c Release -r $(cat /rid) --self-contained --no-restore \
      -p:PublishSingleFile=true -p:DebugType=none -o /out

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine
COPY --from=build /out/memserve /usr/local/bin/memserve
# defaults; every option can be overridden with MEMSERVE_* env vars or CLI args
ENV MEMSERVE_HTTP_PORT=80 \
    MEMSERVE_HTTPS_PORT=443 \
    MEMSERVE_FTP_PORT=21 \
    MEMSERVE_FTP_PASV_PORTS=50000-50100 \
    MEMSERVE_BACKING=1G
EXPOSE 80 443 21 50000-50100
HEALTHCHECK --interval=10s --timeout=3s CMD wget -qO- http://127.0.0.1:${MEMSERVE_HTTP_PORT}/health || exit 1
ENTRYPOINT ["/usr/local/bin/memserve"]

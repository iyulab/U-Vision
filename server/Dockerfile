# U-Vision engine server — the PWA and the API in one process (src/UVision.Api).
# Build from this directory:  docker build -t u-vision:<version> .
# Run:  docker run -p 8080:8080 -v u-vision-data:/data u-vision:<version>
# Everything the server keeps (scenarios, captures, results, ledger, the one-time admin setup token)
# lives under /data — mount a volume there, or it is lost with the container.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Publish for the image's own platform only: without a runtime identifier every OS's native libraries
# (SQLite, Skia, ...) ride along and the image is several times larger.
ARG TARGETARCH
WORKDIR /src
RUN case "$TARGETARCH" in arm64) echo linux-arm64 ;; *) echo linux-x64 ;; esac > /tmp/rid
# Restore first so the package layer is reused while only sources change.
COPY nuget.config UVision.slnx ./
COPY src/UVision.Hosting/UVision.Hosting.csproj src/UVision.Hosting/
COPY src/UVision.Api/UVision.Api.csproj src/UVision.Api/
RUN dotnet restore src/UVision.Api/UVision.Api.csproj -r "$(cat /tmp/rid)"
COPY src/UVision.Hosting/ src/UVision.Hosting/
COPY src/UVision.Api/ src/UVision.Api/
RUN dotnet publish src/UVision.Api/UVision.Api.csproj -c Release --no-restore -r "$(cat /tmp/rid)" --self-contained false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
# The image's non-root user owns the data directory; the server never runs as root.
RUN mkdir /data && chown "$APP_UID" /data
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Storage__DataPath=/data
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "UVision.Api.dll"]

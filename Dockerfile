FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Matddns.csproj ./
RUN dotnet restore Matddns.csproj
COPY src/ ./
RUN dotnet publish Matddns.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ARG VERSION=local
ARG BUILD=local
ARG BUILD_DATE=
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV MATDDNS_DATA=/data
ENV MATDDNS_VERSION=$VERSION
ENV MATDDNS_BUILD=$BUILD
ENV MATDDNS_BUILD_DATE=$BUILD_DATE
EXPOSE 8080
# tzdata so configurable time zones resolve (otherwise .NET only knows UTC); curl for the container healthcheck
RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data && useradd -u 10001 -m matddns && chown -R matddns:matddns /data /app
COPY --from=build --chown=matddns:matddns /app ./
USER matddns
# Healthy while /api/health answers 200: ok or degraded (a source or a DNS provider failing); 503 only when Matddns cannot do its job
# (config.json unreadable). The health of the Mat platform contract (MatDesign docs/PLATFORM.md): a degraded app must not count as unhealthy.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS -o /dev/null http://127.0.0.1:8080/api/health || exit 1
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "Matddns.dll"]

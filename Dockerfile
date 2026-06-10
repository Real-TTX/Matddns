FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Matddns.csproj ./
RUN dotnet restore Matddns.csproj
COPY src/ ./
RUN dotnet publish Matddns.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
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
# tzdata so configurable time zones resolve (otherwise .NET only knows UTC)
RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data && useradd -u 10001 -m matddns && chown -R matddns:matddns /data /app
COPY --from=build --chown=matddns:matddns /app ./
USER matddns
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "Matddns.dll"]

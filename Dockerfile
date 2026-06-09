FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Matddns.csproj ./
RUN dotnet restore Matddns.csproj
COPY src/ ./
RUN dotnet publish Matddns.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV MATDDNS_DATA=/data
EXPOSE 8080
RUN mkdir -p /data && useradd -u 10001 -m matddns && chown -R matddns:matddns /data /app
COPY --from=build --chown=matddns:matddns /app ./
USER matddns
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "Matddns.dll"]

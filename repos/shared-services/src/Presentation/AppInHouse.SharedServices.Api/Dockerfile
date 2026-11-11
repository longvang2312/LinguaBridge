# AppInHouse Shared Services API (port 5030).
# Build context: repos/shared-services (the folder with Directory.Build.props), for example
#   docker build -f src/Presentation/AppInHouse.SharedServices.Api/Dockerfile -t appinhouse/shared-services-api .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, with only the project files, so the packages are cached until a project file changes.
COPY Directory.Build.props ./
COPY src/Core/AppInHouse.SharedServices.Domain/*.csproj src/Core/AppInHouse.SharedServices.Domain/
COPY src/Core/AppInHouse.SharedServices.Application/*.csproj src/Core/AppInHouse.SharedServices.Application/
COPY src/Infrastructure/AppInHouse.SharedServices.Infrastructure/*.csproj src/Infrastructure/AppInHouse.SharedServices.Infrastructure/
COPY src/Infrastructure/AppInHouse.SharedServices.Persistence/*.csproj src/Infrastructure/AppInHouse.SharedServices.Persistence/
COPY src/Presentation/AppInHouse.SharedServices.Api/*.csproj src/Presentation/AppInHouse.SharedServices.Api/
RUN dotnet restore src/Presentation/AppInHouse.SharedServices.Api/AppInHouse.SharedServices.Api.csproj

COPY src/ src/
RUN dotnet publish src/Presentation/AppInHouse.SharedServices.Api/AppInHouse.SharedServices.Api.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
# curl is what the health check uses; the base image does not have it.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# Two folders must outlive the container, so mount a volume on each:
#   /root/.aspnet/DataProtection-Keys  the key ring that protects the stored secrets (lose it and every stored secret is unreadable)
#   /data/storage                      the files of the local-disk storage provider
RUN mkdir -p /root/.aspnet/DataProtection-Keys /data/storage
VOLUME ["/root/.aspnet/DataProtection-Keys", "/data/storage"]

ENV ASPNETCORE_URLS=http://+:5030 \
    DataProtection__KeysPath=/root/.aspnet/DataProtection-Keys \
    Storage__BasePath=/data/storage

EXPOSE 5030
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=5 \
    CMD curl --fail --silent http://localhost:5030/health || exit 1

ENTRYPOINT ["dotnet", "AppInHouse.SharedServices.Api.dll"]

# AppInHouse Shared Services management portal (port 5031).
# Build context: repos/shared-services (the folder with Directory.Build.props), for example
#   docker build -f src/Presentation/AppInHouse.SharedServices.Web/Dockerfile -t appinhouse/shared-services-web .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# The portal only knows the client SDK (it has no reference to the service's own projects).
COPY Directory.Build.props ./
COPY src/Sdk/AppInHouse.SharedServices.ClientSdk/*.csproj src/Sdk/AppInHouse.SharedServices.ClientSdk/
COPY src/Presentation/AppInHouse.SharedServices.Web/*.csproj src/Presentation/AppInHouse.SharedServices.Web/
RUN dotnet restore src/Presentation/AppInHouse.SharedServices.Web/AppInHouse.SharedServices.Web.csproj

COPY src/ src/
RUN dotnet publish src/Presentation/AppInHouse.SharedServices.Web/AppInHouse.SharedServices.Web.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# The anti-forgery keys live in the Data Protection ring; a volume keeps the forms that are open valid across a restart.
RUN mkdir -p /root/.aspnet/DataProtection-Keys
VOLUME ["/root/.aspnet/DataProtection-Keys"]

ENV ASPNETCORE_URLS=http://+:5031 \
    DataProtection__KeysPath=/root/.aspnet/DataProtection-Keys

EXPOSE 5031
HEALTHCHECK --interval=15s --timeout=5s --start-period=20s --retries=5 \
    CMD curl --fail --silent http://localhost:5031/health || exit 1

ENTRYPOINT ["dotnet", "AppInHouse.SharedServices.Web.dll"]

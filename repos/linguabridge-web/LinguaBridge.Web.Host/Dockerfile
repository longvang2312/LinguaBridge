# Build context: repos/linguabridge-web (the web client files, with this host in LinguaBridge.Web.Host). Build with:
#   docker build -f LinguaBridge.Web.Host/Dockerfile -t appinhouse-linguabridge-web:local .

# ---- Stage 1: publish the host ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY LinguaBridge.Web.Host/*.csproj LinguaBridge.Web.Host/
RUN dotnet restore LinguaBridge.Web.Host/LinguaBridge.Web.Host.csproj
COPY LinguaBridge.Web.Host/ LinguaBridge.Web.Host/
RUN dotnet publish LinguaBridge.Web.Host/LinguaBridge.Web.Host.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---- Stage 2: runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# The web client files (see .dockerignore) are served from /web; the host itself is not part of them.
COPY . /web
RUN rm -rf /web/LinguaBridge.Web.Host
COPY --from=build /app/publish .

ENV Web__Root=/web \
    Kestrel__Endpoints__Http__Url=http://0.0.0.0:3000
EXPOSE 3000

USER app
ENTRYPOINT ["dotnet", "LinguaBridge.Web.Host.dll"]

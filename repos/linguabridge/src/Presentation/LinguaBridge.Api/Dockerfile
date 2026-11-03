# Build context: repos/linguabridge. Build with:
#   docker build -f src/Presentation/LinguaBridge.Api/Dockerfile -t appinhouse-linguabridge-api:local .

# ---- Stage 1: restore, build, publish ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files first, so `dotnet restore` is cached until a dependency actually changes.
COPY Directory.Build.props ./
COPY src/Core/LinguaBridge.Domain/*.csproj src/Core/LinguaBridge.Domain/
COPY src/Core/LinguaBridge.Application/*.csproj src/Core/LinguaBridge.Application/
COPY src/Modules/LinguaBridge.Modules.Content/*.csproj src/Modules/LinguaBridge.Modules.Content/
COPY src/Modules/LinguaBridge.Modules.Learning/*.csproj src/Modules/LinguaBridge.Modules.Learning/
COPY src/Modules/LinguaBridge.Modules.AiTutor/*.csproj src/Modules/LinguaBridge.Modules.AiTutor/
COPY src/Modules/LinguaBridge.Modules.Billing/*.csproj src/Modules/LinguaBridge.Modules.Billing/
COPY src/Infrastructure/LinguaBridge.Infrastructure.Persistence/*.csproj src/Infrastructure/LinguaBridge.Infrastructure.Persistence/
COPY src/Infrastructure/LinguaBridge.Infrastructure.External/*.csproj src/Infrastructure/LinguaBridge.Infrastructure.External/
COPY src/Presentation/LinguaBridge.Api/*.csproj src/Presentation/LinguaBridge.Api/
RUN dotnet restore src/Presentation/LinguaBridge.Api/LinguaBridge.Api.csproj

COPY src/ src/

WORKDIR /src/src/Presentation/LinguaBridge.Api
RUN dotnet publish -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---- Stage 2: runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Stated here so the port does not depend on the base image's default.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app/publish .

# The base image ships an unprivileged "app" user; the service has no reason to run as root.
USER app
ENTRYPOINT ["dotnet", "LinguaBridge.Api.dll"]

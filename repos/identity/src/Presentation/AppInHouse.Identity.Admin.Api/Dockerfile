# Build context: repos/identity (see TASK-017). Build with:
#   docker build -f src/Presentation/AppInHouse.Identity.Admin.Api/Dockerfile -t appinhouse-identity-admin-api:local .

# ---- Stage 1: restore, build, publish ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Project files first, so `dotnet restore` is cached until a dependency actually changes.
COPY Directory.Build.props ./
COPY src/Core/AppInHouse.Identity.SharedKernel/*.csproj src/Core/AppInHouse.Identity.SharedKernel/
COPY src/Core/AppInHouse.Identity.Domain/*.csproj src/Core/AppInHouse.Identity.Domain/
COPY src/Core/AppInHouse.Identity.Application/*.csproj src/Core/AppInHouse.Identity.Application/
COPY src/Infrastructure/AppInHouse.Identity.Infrastructure.Identity/*.csproj src/Infrastructure/AppInHouse.Identity.Infrastructure.Identity/
COPY src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence/*.csproj src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence/
COPY src/Infrastructure/AppInHouse.Identity.Infrastructure.Caching/*.csproj src/Infrastructure/AppInHouse.Identity.Infrastructure.Caching/
COPY src/Infrastructure/AppInHouse.Identity.Infrastructure.Logging/*.csproj src/Infrastructure/AppInHouse.Identity.Infrastructure.Logging/
COPY src/Presentation/AppInHouse.Identity.Admin.Api/*.csproj src/Presentation/AppInHouse.Identity.Admin.Api/
RUN dotnet restore src/Presentation/AppInHouse.Identity.Admin.Api/AppInHouse.Identity.Admin.Api.csproj

COPY src/ src/

WORKDIR /src/src/Presentation/AppInHouse.Identity.Admin.Api
RUN dotnet publish -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---- Stage 2: runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# ASP.NET Core 8 images listen on 8080 by default; stated here so it does not depend on the base image.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app/publish .

# The base image ships an unprivileged "app" user; the service has no reason to run as root.
USER app
ENTRYPOINT ["dotnet", "AppInHouse.Identity.Admin.Api.dll"]

# syntax=docker/dockerfile:1

# Build stage: publishes the Web API and, alongside it, an EF Core migrations bundle. The bundle is
# built here because applying migrations needs the SDK and the dotnet-ef tool, neither of which the
# runtime image carries -- the bundle is a plain executable that the entrypoint can run before the
# API starts, so the deployed container never needs the SDK.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Restored first and on their own so the tool and package layers survive a source-only change.
COPY .config/dotnet-tools.json .config/
RUN dotnet tool restore

# Central package management: the project files name their packages and this file alone gives the
# versions, so restore fails outright without it. It belongs in this layer rather than with the
# sources below for the same reason the csproj files do — a source-only change must not invalidate
# the restore cache, and a version change should.
COPY Directory.Packages.props ./

COPY src/Presentation/ArturRios.Heimdall.WebApi/ArturRios.Heimdall.WebApi.csproj src/Presentation/ArturRios.Heimdall.WebApi/
COPY src/Application/ArturRios.Heimdall.Command/ArturRios.Heimdall.Command.csproj src/Application/ArturRios.Heimdall.Command/
COPY src/Application/ArturRios.Heimdall.Query/ArturRios.Heimdall.Query.csproj src/Application/ArturRios.Heimdall.Query/
COPY src/Application/ArturRios.Heimdall.Shared/ArturRios.Heimdall.Shared.csproj src/Application/ArturRios.Heimdall.Shared/
COPY src/Domain/ArturRios.Heimdall.Domain/ArturRios.Heimdall.Domain.csproj src/Domain/ArturRios.Heimdall.Domain/
COPY src/Infrastructure/ArturRios.Heimdall.Data/ArturRios.Heimdall.Data.csproj src/Infrastructure/ArturRios.Heimdall.Data/
RUN dotnet restore src/Presentation/ArturRios.Heimdall.WebApi/ArturRios.Heimdall.WebApi.csproj

COPY src/ src/

RUN dotnet publish src/Presentation/ArturRios.Heimdall.WebApi/ArturRios.Heimdall.WebApi.csproj \
        --configuration Release --no-restore --output /app

# Framework-dependent on purpose: the runtime image already ships the .NET runtime the bundle needs,
# so there is no reason to duplicate it. DesignTimeDbContextFactory reads the connection string from
# HEIMDALL_DATA_CONNECTIONSTRING, which the entrypoint passes through from the container's
# environment -- the same variable the API itself uses.
#
# The placeholder connection string is what DesignTimeDbContextFactory needs to hand `dotnet ef` a
# DbContext while the bundle is built; nothing connects here, and the bundle uses the --connection
# the entrypoint passes it instead.
RUN HEIMDALL_DATA_CONNECTIONSTRING="Host=localhost;Port=5432;Database=heimdall;Username=postgres;Password=postgres" \
    dotnet ef migrations bundle \
        --project src/Infrastructure/ArturRios.Heimdall.Data \
        --startup-project src/Infrastructure/ArturRios.Heimdall.Data \
        --configuration Release \
        --output /app/heimdall-migrate \
        --force

# Runtime stage.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# curl is installed for the container health check alone: the runtime image ships neither curl nor
# wget, and without one Compose has no way to tell a started container from a ready one -- which is
# what "depends_on: service_healthy" needs to be meaningful for anything placed in front of the API.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .
COPY docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh /app/heimdall-migrate

# Serilog writes under this directory (HEIMDALL_LOG_DIRECTORY) and the seeder needs nothing writable,
# so the container can drop to the image's non-root user once the directory belongs to it.
ENV HEIMDALL_LOG_DIRECTORY=/app/logs
RUN mkdir -p /app/logs && chown -R $APP_UID:$APP_UID /app/logs
USER $APP_UID

# Matches the aspnet image's own default; named here so the Compose port mapping has an explicit
# counterpart to point at.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
CMD ["dotnet", "ArturRios.Heimdall.WebApi.dll"]

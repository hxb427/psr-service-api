# syntax=docker/dockerfile:1.7
# -----------------------------------------------------------------------------
# Stage 1: build & publish
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore is cached by copying csproj + central props first
COPY Directory.Packages.props Directory.Build.props ./
COPY src/PSR.Service.Api/PSR.Service.Api.csproj src/PSR.Service.Api/
RUN dotnet restore src/PSR.Service.Api/PSR.Service.Api.csproj

# Copy the rest and publish
COPY src/PSR.Service.Api/ src/PSR.Service.Api/
RUN dotnet publish src/PSR.Service.Api/PSR.Service.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# -----------------------------------------------------------------------------
# Stage 2: runtime
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl is needed for the docker-compose HEALTHCHECK — the aspnet base image does NOT ship it.
# Without this the container is stuck reporting `health: starting` forever.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

# Create directories with permissions for the non-root app user (UID set by base image)
RUN mkdir -p /app/logs && chown -R $APP_UID:$APP_UID /app

USER $APP_UID

COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .

# Non-root cannot bind to <1024; container listens on 8443, docker-compose maps host 443
EXPOSE 8443

ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "PSR.Service.Api.dll"]

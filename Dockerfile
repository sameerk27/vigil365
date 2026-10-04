# syntax=docker/dockerfile:1
# Multi-stage build: compile the React frontend, publish the .NET API, run on Linux.
# Build context is the repo root:  docker build -t vigil365 .

# ---- 1. Build the frontend (vite outputs into the API's wwwroot) ----
FROM node:20-alpine AS frontend
WORKDIR /src/m365-security-dashboard-client
COPY src/m365-security-dashboard-client/package*.json ./
RUN npm install --no-audit --no-fund
COPY src/m365-security-dashboard-client/ ./
RUN npm run build
# vite is configured with outDir ../M365SecurityDashboard.Api/wwwroot,
# so the bundle lands at /src/M365SecurityDashboard.Api/wwwroot
# The e2e build bypasses Microsoft sign-in (VITE_E2E_FAKE_AUTH). A client .env
# file that sets it would bake the bypass into this image - refuse to ship it.
RUN if grep -rqE 'vigil365-e2e-fake-auth|VITE_E2E_FAKE_AUTH' /src/M365SecurityDashboard.Api/wwwroot; then \
      echo "The client bundle contains the e2e sign-in bypass (VITE_E2E_FAKE_AUTH)." >&2; exit 1; \
    fi

# ---- 2. Publish the API, including the built wwwroot ----
# Laid out as in the repository: the csproj embeds ../../graph-permissions.json.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /repo
COPY graph-permissions.json ./
COPY src/M365SecurityDashboard.Api/ ./src/M365SecurityDashboard.Api/
COPY --from=frontend /src/M365SecurityDashboard.Api/wwwroot ./src/M365SecurityDashboard.Api/wwwroot
RUN dotnet publish src/M365SecurityDashboard.Api/M365SecurityDashboard.Api.csproj -c Release -o /app

# ---- 3. Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Container serves HTTP on 8080; TLS is terminated by a reverse proxy (compose/ingress).
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Security__RequireHttps=false \
    DataProtection__KeyPath=/keys

# Persist the Data Protection key ring so encrypted secrets survive restarts.
VOLUME ["/keys", "/app/logs"]
EXPOSE 8080

ENTRYPOINT ["dotnet", "M365SecurityDashboard.Api.dll"]

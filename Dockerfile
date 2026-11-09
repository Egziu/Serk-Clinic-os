# SERK Clinic OS — container image (SPA + API in one ASP.NET Core host).
# Used for the hosted synthetic "Demo" environment; see docs/operations/RAILWAY_DEMO.md. Not a production profile.

# --- 1. Web (React + TypeScript) -> src/backend/Serk.Clinic.Host/wwwroot
FROM node:22.23.4-bookworm-slim AS web
WORKDIR /src/src/web
COPY src/web/package.json src/web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/web/ ./
RUN npm run build

# --- 2. Backend (.NET 10) publish
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props SerkClinic.slnx ./
COPY src/ src/
COPY database/ database/
COPY --from=web /src/src/backend/Serk.Clinic.Host/wwwroot/ src/backend/Serk.Clinic.Host/wwwroot/
RUN dotnet restore src/backend/Serk.Clinic.Host/Serk.Clinic.Host.csproj --locked-mode \
 && dotnet publish src/backend/Serk.Clinic.Host/Serk.Clinic.Host.csproj -c Release --no-restore -o /app

# --- 3. Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
COPY deploy/entrypoint.sh /usr/local/bin/serk-entrypoint
RUN chmod 0755 /usr/local/bin/serk-entrypoint && mkdir -p /var/lib/serk/objects && chown -R app:app /var/lib/serk
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0 \
    ObjectStore__RootPath=/var/lib/serk/objects
EXPOSE 8080
# Starts as root only to take ownership of a freshly mounted volume, then drops to the unprivileged "app" user.
ENTRYPOINT ["/usr/local/bin/serk-entrypoint"]

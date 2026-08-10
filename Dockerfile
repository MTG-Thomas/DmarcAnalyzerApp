# syntax=docker/dockerfile:1

FROM node:22-alpine AS web-build
WORKDIR /web
RUN npm install -g npm@11.6.2
COPY src/web/package*.json ./
RUN npm ci
COPY src/web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /src

# The commit this image is built from, stamped into the assembly's
# InformationalVersion as "<Version>+<sha>" and surfaced in the UI, the
# /api/v1/system/status payload and service.version. It has to be passed in:
# .dockerignore excludes .git (correctly — it is large and secret-adjacent), so
# the SDK cannot read the repository the way it does for a local build.
#
# Empty by design on a release build, which is what makes "0.9.0" mean the
# release and "0.9.0+a1b2c3d" mean a build past it. See ci.yml, which passes this
# on every build except a tag, and Application/Common/AppVersion.cs.
ARG SOURCE_REVISION=""

# Directory.Build.props is where <Version> lives, so the image cannot be built
# without it — and copying it also brings packages.lock.json into effect, hence
# the lockfile alongside the csproj: restore should resolve the same transitive
# versions here as it does in CI, not whatever is current at build time.
COPY src/Directory.Build.props ./
COPY src/api/DmarcAnalyzer.Api.csproj src/api/packages.lock.json ./api/
RUN dotnet restore ./api/DmarcAnalyzer.Api.csproj
COPY src/api/ ./api/
RUN dotnet publish ./api/DmarcAnalyzer.Api.csproj -c Release -o /out --no-restore \
    -p:SourceRevisionId="$SOURCE_REVISION"

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Links the GHCR package to this repository (and powers "view source" on ghcr.io).
LABEL org.opencontainers.image.source="https://github.com/dmarc-analyzer-net/DmarcAnalyzerApp" \
      org.opencontainers.image.description="Open-source, self-hosted, agency-first DMARC analyzer" \
      org.opencontainers.image.licenses="Apache-2.0"
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 curl \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd -r dmarcanalyzer && useradd -r -g dmarcanalyzer dmarcanalyzer
COPY --from=dotnet-build --chown=dmarcanalyzer:dmarcanalyzer /out ./
COPY --from=web-build --chown=dmarcanalyzer:dmarcanalyzer /web/dist ./wwwroot

ENV ASPNETCORE_URLS=http://+:8080
ENV APP_MODE=api

USER dmarcanalyzer
EXPOSE 8080

ENTRYPOINT ["dotnet", "DmarcAnalyzer.Api.dll"]

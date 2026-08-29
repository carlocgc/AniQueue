# Production image for AniQueue. Multi-stage on purpose: the SDK is
# nearly ten times the size of the runtime and carries a compiler, so it builds
# and is then left behind.
#
# Written here rather than generated. Visual Studio's Container Tools offers to
# write one; this is the production Dockerfile and VS points at it rather than
# replacing it. What VS does
# need is the stage order below: its fast-mode debugging targets the *first*
# stage in the file, so `base` comes first and is the runtime image. Put `build`
# first and pressing F5 on the Docker profile would debug inside the SDK image.

# ---------------------------------------------------------------------------
# base — the runtime, and what Visual Studio attaches to
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base

# curl is here for one reason: the health check below runs *inside* the
# container, and the aspnet image ships neither curl nor wget. A few megabytes
# buys a container that reports itself unhealthy rather than merely running, and
# gives an operator something to debug a reverse proxy with.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# 8080 rather than 80, because a non-root process cannot bind a privileged port.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# The volume holding everything that must outlive the container: the SQLite
# database, the userconfig.json written beside it, the cached cover art
# under /data/art and the signing keys under /data/keys. Created and owned
# here so that a *named* volume inherits the ownership from the image. A bind
# mount does not inherit it, which is why the entry point below takes ownership
# at start rather than the image trying to.
#
# APP_UID is defined by the base image as 1654. Named explicitly rather than
# assumed, so the entry point and the image agree on one number.
RUN mkdir -p /data && chown -R $APP_UID:$APP_UID /data

# No USER here, deliberately. The entry point starts as root purely to take
# ownership of /data, then drops to $APP_UID for the application itself, so the
# process serving requests is unprivileged either way. Setting USER would remove
# the one privilege that makes a bind mount work without host setup.
COPY docker-entrypoint.sh /usr/local/bin/
RUN chmod +x /usr/local/bin/docker-entrypoint.sh

# ---------------------------------------------------------------------------
# build
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# Release unless somebody says otherwise, which in practice means Visual Studio:
# its Docker profile passes the configuration the solution is set to. A container
# built from a Debug solution should carry Debug symbols, or a breakpoint in it
# lands somewhere unhelpful. Compose and CI pass nothing and get Release.
ARG BUILD_CONFIGURATION=Release

WORKDIR /src

# Project files first, so a source-only change reuses the restore layer. The two
# Directory.*.props come with them because central package management puts
# every version in Directory.Packages.props — without it restore has no versions
# to resolve and fails before it reads a single package reference.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/AniQueue.Core/AniQueue.Core.csproj src/AniQueue.Core/
COPY src/AniQueue.Infrastructure/AniQueue.Infrastructure.csproj src/AniQueue.Infrastructure/
COPY src/AniQueue.Web/AniQueue.Web.csproj src/AniQueue.Web/
RUN dotnet restore src/AniQueue.Web/AniQueue.Web.csproj

COPY src/ src/

# UseAppHost=false drops the native launcher nothing here starts: the entry point
# below runs `dotnet AniQueue.Web.dll`, so the executable would be dead weight.
#
# **No --no-restore here, and it cost a broken published image to learn why.** The
# restore above is staged from project files alone so that a source-only change
# reuses its layer, and a publish told to skip restoring on top of that one drops
# the framework's own static web assets — `wwwroot/_framework`, which is where
# `blazor.web.js` lives. Nothing fails. The image builds, starts, reports healthy
# and serves a page that renders correctly and then does absolutely nothing,
# because the script that opens the circuit 404s.
#
# Letting publish restore again costs almost nothing: the packages are already in
# the image's NuGet cache from the layer above, so it re-resolves rather than
# re-downloads, and the layer caching that the staged copy exists for is kept.
RUN dotnet publish src/AniQueue.Web/AniQueue.Web.csproj \
    --configuration $BUILD_CONFIGURATION \
    --output /app/publish \
    -p:UseAppHost=false

# The assertion that makes the above a build failure rather than a silent one. A
# missing interactive script cannot be caught by anything that only checks the
# container starts, and `/health` answers happily without it.
RUN test -f /app/publish/wwwroot/_framework/blazor.web.js \
    || (echo "Publish produced no wwwroot/_framework/blazor.web.js; the page would render and never become interactive." && exit 1)

# ---------------------------------------------------------------------------
# final — what is published and what compose runs
# ---------------------------------------------------------------------------
FROM base AS final
WORKDIR /app
COPY --from=build /app/publish ./

# --start-period covers migrate-on-boot: a first run against an empty volume
# applies every migration before anything serves, and a container reported
# unhealthy for doing exactly what it should would restart itself mid-migration.
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
    CMD curl --fail --silent --show-error http://localhost:8080/health || exit 1

ENTRYPOINT ["docker-entrypoint.sh", "dotnet", "AniQueue.Web.dll"]

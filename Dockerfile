# Production image for AniQueue (Phase 11). Multi-stage on purpose: the SDK is
# nearly ten times the size of the runtime and carries a compiler, so it builds
# and is then left behind (§13).
#
# **Written here, not generated.** §13 says not to accept the Dockerfile Visual
# Studio's Container Tools offers to write, and that still holds — this file is
# the production one and VS points at it rather than replacing it. What VS does
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
# database, the userconfig.json written beside it (D20), the cached cover art
# under /data/art (D47) and the signing keys under /data/keys. Created and owned
# here so that a *named* volume inherits the ownership from the image — a bind
# mount does not, and its host directory has to be chowned to this UID by hand
# (§9). That is the Unraid case, and the README says so.
#
# APP_UID is defined by the base image as 1654. Named explicitly rather than
# assumed, so the number the README tells people to chown to comes from the same
# place the container actually uses.
RUN mkdir -p /data && chown -R $APP_UID:$APP_UID /data
USER $APP_UID

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
# Directory.*.props come with them because central package management (D6) puts
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
RUN dotnet publish src/AniQueue.Web/AniQueue.Web.csproj \
    --configuration $BUILD_CONFIGURATION \
    --no-restore \
    --output /app/publish \
    -p:UseAppHost=false

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

ENTRYPOINT ["dotnet", "AniQueue.Web.dll"]

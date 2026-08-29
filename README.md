<div align="center">

<img src="src/AniQueue.Web/wwwroot/favicon.svg" width="88" alt="">

# AniQueue

**Self-hosted anime watchlist and backlog decision layer.**

Your library already lives on MyAnimeList or AniList. AniQueue answers the question
those tools answer badly: *what do I actually watch next?*

[![PR build](https://github.com/carlocgc/AniQueue/actions/workflows/pr-build.yml/badge.svg)](https://github.com/carlocgc/AniQueue/actions/workflows/pr-build.yml)
[![Development image](https://github.com/carlocgc/AniQueue/actions/workflows/dev-image.yml/badge.svg)](https://github.com/carlocgc/AniQueue/actions/workflows/dev-image.yml)
[![Release](https://github.com/carlocgc/AniQueue/actions/workflows/release-docker.yml/badge.svg)](https://github.com/carlocgc/AniQueue/actions/workflows/release-docker.yml)

[![Docker image](https://img.shields.io/docker/image-size/carlocgc/aniqueue/dev?label=docker%20image)](https://hub.docker.com/r/carlocgc/aniqueue)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![Licence: AGPL-3.0](https://img.shields.io/badge/licence-AGPL--3.0-blue)](LICENSE)
[![Status: pre-MVP](https://img.shields.io/badge/status-pre--MVP-orange)](docs/ROADMAP.md)

</div>

---

## ⚠️ Do not expose AniQueue to the internet

**AniQueue has had no security audit, and it has no authentication of any kind.**
Anyone who can reach the port can read and change everything in it.

Run it on a network you trust — a home LAN, or behind a VPN such as Tailscale or
WireGuard. Do not put it on a public IP, and do not publish it through a reverse proxy
or a tunnel without putting your own authentication in front of it first.

There is also no release yet. The only published image is `carlocgc/aniqueue:dev`,
which is rebuilt from the `development` branch on every merge and overwritten each
time. It is the author's own moving edge, not something to keep data in.

---

## What it does

- **Import a MyAnimeList XML export**, or sync a public AniList list on a schedule.
  Nothing is ever written back to either service.
- **See what each title comes with** — prequels, sequels, side stories, spin-offs —
  and queue a show and its sequels in one click.
- **Keep a deliberate, hand-ordered *Up Next* queue.** Titles leave it on their own
  once you have started or finished them, so it stays true without maintenance.
- **Rank the backlog against your own historical scores** rather than global
  popularity, using an external LLM of your choice. No account and no API key: paste
  the request into any chat window, or point AniQueue at a model you host yourself.
- **Run it on your own hardware.** One container, one SQLite file, no cloud
  dependency.

## Running it

```bash
docker compose up -d
```

Then open `http://localhost:8377`. The first start creates the database, applies every
migration and writes a `userconfig.json` beside it. The library starts empty, and the
first screen offers an AniList sync or a MyAnimeList import.

To build the image from a checkout instead of pulling it, add `--build`.

### The volume

Everything that must survive the container lives in one volume: `aniqueue.db`, the
`userconfig.json` you edit when the pages cannot be reached, the cached cover art
under `art/`, and the signing keys under `keys/` that keep open browser pages working
across an upgrade. Recreating the container keeps all of it; deleting the volume
deletes your library. Backup and restore are a copy of that volume with the container
stopped.

The compose file uses a **named volume** deliberately. AniQueue runs as UID **1654**,
and Docker copies ownership from the image into a named volume, so it works with no
setup. A **bind mount** is not seeded that way — if you swap the volume for a host
path (the usual Unraid arrangement), chown it first or AniQueue cannot create its
database:

```bash
chown -R 1654:1654 /mnt/user/appdata/aniqueue
```

### Settings

Every setting lives in `userconfig.json` in the volume. AniQueue writes it whenever
you change something in the application, and you can edit it by hand — which is how
you change its behaviour when its own pages cannot be reached. It holds every key it
accepts with a line saying what each one does, and it is rewritten whole on every
save, so anything else you put in it will not survive.

The compose file holds the container's concerns only: the port, the volume and the
log limits.

### Logs

Logs go to stdout and nowhere else, so `docker logs aniqueue` is the whole of it. The
compose file caps them at three 10 MB files, because Docker's default driver does not
rotate.

Startup prints where the data is and what this installation is configured to do — the
database and settings paths, whether sync is on and which AniList account it reads,
whether scheduled scoring is on and where it points, and the background task cadence.
Most "why is it not doing anything" questions are answered by those four lines.

For more, turn on AniQueue's own debug logging. It says why each background task
decided it had nothing to do, which filters a page ran, and why an image came back
404:

```bash
docker run -e Logging__LogLevel__AniQueue=Debug ...
```

The container also reports a health check against `/health`, so `docker ps` says
`healthy` rather than just `running`. It allows 40 seconds at startup, because a first
run applies every migration before it serves anything.

### If you get a blank page

Try it with browser extensions off. Anything that rewrites the page as it loads — a
dark-mode extension, a translator, an accessibility overlay, Brave's Shields — can
break the live connection this application renders through, and the symptom is an
error banner or nothing at all. AniQueue ships Dark Reader's own opt-out tag and has a
dark theme already, so that one is handled; the rest are not, and the application
cannot detect them.

## Development

Requires the .NET 10 SDK, and Visual Studio 2026 or the `dotnet` CLI.

```bash
dotnet tool restore
dotnet build
dotnet test
```

Run it with `F5` on `AniQueue.Web`, or:

```bash
dotnet run --project src/AniQueue.Web
```

The development database is created at `src/AniQueue.Web/data/aniqueue.db` on first
run and starts empty. For a surface that needs rows in it, use the sample profile
instead — it has its own database and its own settings file under
`src/AniQueue.Web/data/sample/`, so it cannot touch a real library:

```bash
dotnet run --project src/AniQueue.Web --launch-profile "http (sample data)"
```

To reach the development server from a phone on the same network, use the *http (lan)*
profile. Windows will want an inbound rule for the port. It is a plain-http server
with no authentication, so it is for a network you trust and nothing else.

There is a **Docker** profile in the Visual Studio run dropdown. It builds and runs
this same Dockerfile — not one Container Tools writes — and attaches the debugger, so
a breakpoint can be hit inside the container. It uses a volume of its own.

## Technology

.NET 10 · ASP.NET Core Blazor Web App (Interactive Server) · EF Core 10 · SQLite ·
xUnit · Docker. No React, Angular, Vue, or separate frontend build system.

## Documentation

| Document | Purpose |
|---|---|
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | The plan and the architecture: domain model, service boundaries, decisions, phases |
| [`docs/RELEASE-NOTES.md`](docs/RELEASE-NOTES.md) | Changes that alter data or behaviour, worth reading before upgrading |
| [`docs/BUILD-PROMPT.md`](docs/BUILD-PROMPT.md) | The original project brief, preserved for reference |
| [`CLAUDE.md`](CLAUDE.md) | Working conventions: the development database, verification, testing, platform gotchas |

## Licence

[GNU Affero General Public License v3.0](LICENSE). AniQueue is meant to be run as a
service on hardware you control, and the AGPL is what keeps a modified version served
to other people open as well.

# AniQueue

Self-hosted anime watchlist and **backlog decision layer**.

AniQueue is not a MyAnimeList or AniList replacement. It assumes your library already lives
somewhere and answers the question those tools answer badly: **what do I actually watch
next?**

- Import a MyAnimeList XML export, or sync a public AniList list on a schedule.
- See what each title is related to — prequels, sequels, side stories, spin-offs — on the row
  itself, and queue a show and its sequels in one click.
- Maintain a deliberate, hand-ordered **Up Next** queue.
- Rank the backlog against **your own historical scores**, not global popularity — using an
  external LLM of your choice, with **no API key required**.
- Run it on your own hardware. No cloud dependency.

## Status

**In development — pre-MVP.** See [`docs/ROADMAP.md`](docs/ROADMAP.md) for the phase plan,
architectural decisions and acceptance criteria. There is no release yet — the container runs,
and `carlocgc/aniqueue:dev` is the moving edge of the `development` branch rather than something
to keep data in.

| Phase | | |
|---|---|---|
| 0 | Foundation — solution, projects, build settings | **complete** |
| 1 | Domain model, EF Core + SQLite, migrations, seed data | **complete** |
| 2 | MyAnimeList XML import → preview → commit → backlog | **complete** |
| 3 | Backlog page — filters, sorting, bulk actions | **complete** |
| 4 | Up Next queue — manual ordering, drag and drop | **complete** |
| 5 | AniList read sync — reconciliation, on demand, then unattended | **complete** |
| 6 | Relations — a title's prequels, sequels and spin-offs | **complete** |
| 7 | Scoring interchange — export the backlog, bring a ranking back | **complete** |
| 8 | Hosted model scoring — settings store, endpoint, surface, scheduled sweep | **complete** |
| 9 | Artwork, AniList enrichment, show detail dialog | **complete** |
| 18 | Mobile first | **complete** |
| 11 | Docker image, compose, health check, one migration baseline | **complete** |
| 13 | CI: build, test, publish | **complete** |
| 10, 12, 14 | Settings page → optional auth → security pass | planned |

## Documentation

| Document | Purpose |
|---|---|
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | Authoritative plan: architecture, domain model, phases, decisions |
| [`docs/RELEASE-NOTES.md`](docs/RELEASE-NOTES.md) | Changes that alter data or behaviour, worth reading before upgrading |
| [`docs/BUILD-PROMPT.md`](docs/BUILD-PROMPT.md) | Original project brief, preserved for reference |
| [`CLAUDE.md`](CLAUDE.md) | Working conventions: the development database, verification, testing, platform gotchas |

## Running it in Docker

**There is no released version yet.** What exists is `carlocgc/aniqueue:dev`, rebuilt from
`development` on every merge and overwritten each time. It is the author's own edge, not a
release: it has not been through the security pass, and it will change under you. `latest` and a
`vX.Y.Z` tag arrive when Phase 14 does.

With that said, it runs:

```bash
docker compose up -d
```

Then open `http://localhost:8080`. The first start creates the database, applies every migration
and writes a `userconfig.json` beside it, and the library is empty — the same first screen a new
user sees, offering an AniList sync or a MyAnimeList import.

To build the image from a checkout instead of pulling it:

```bash
docker compose up -d --build
```

### From Visual Studio

There is a **Docker** profile in the run dropdown. It builds and runs this same Dockerfile — not
one Container Tools writes for you — and attaches the debugger, so a breakpoint can be hit inside
the container. It needs the *Container development tools* component installed with Visual Studio.

It uses a volume of its own, so a debugging session cannot write into the data a real deployment
is using. The ordinary inner loop is still `F5` on `AniQueue.Web` without a container; this is for
the failures that only happen inside one.

### What is in the volume

Everything that must survive the container: `aniqueue.db`, the `userconfig.json` you edit when
the pages cannot be reached, the cached cover art under `art/`, and the signing keys under
`keys/` that keep pages open in a browser working across an upgrade. Recreating the container
keeps all of it. Deleting the volume deletes your library.

The compose file uses a **named volume**, and that is deliberate. The container runs as UID
**1654**, not as root, and Docker copies ownership from the image into a named volume — so it
works with no setup. A **bind mount** is not seeded that way, so if you swap the volume for a host
path (the usual Unraid arrangement) chown it first, or AniQueue cannot create its database:

```bash
chown -R 1654:1654 /mnt/user/appdata/aniqueue
```

### Health and logs

The container reports a health check against `/health`, so `docker ps` says `healthy` rather than
just `running`. It allows 40 seconds at startup, because a first run applies every migration
before it serves anything.

Logs go to stdout and nowhere else — `docker logs aniqueue`. The compose file caps them at three
10 MB files, because Docker's default driver does not rotate and will eventually fill a disk.

### Settings

Everything AniQueue does is set from its own pages, or by editing `userconfig.json` in the volume
and restarting. The compose file holds the container's concerns only: the port, the volume and
the log limits. It sets nothing about AniQueue itself, and it does not need to.

### If you get a blank page

Try it with browser extensions off. Anything that rewrites the page as it loads — a dark-mode
extension, a translator, an accessibility overlay, Brave's Shields — can break the live connection
this application renders through, and the symptom is an error banner or nothing at all rather than
anything that names a cause. AniQueue ships Dark Reader's own opt-out tag and already has a dark
theme, so that one is handled; the rest are not, and there is no way for the application to detect
them.

## Technology

.NET 10 · ASP.NET Core Blazor Web App (Interactive Server) · EF Core 10 · SQLite · xUnit ·
Docker. No React, Angular, Vue, or separate frontend build system.

## Development

Requires the .NET 10 SDK. Visual Studio 2026 or the `dotnet` CLI.

```bash
dotnet tool restore
dotnet build
dotnet test
```

Run it with `F5` on `AniQueue.Web`, or:

```bash
dotnet run --project src/AniQueue.Web
```

The development database is created at `src/AniQueue.Web/data/aniqueue.db` on first run, and
it starts **empty** — the same first screen a new user sees, offering an AniList sync or a
MyAnimeList import. Delete that directory to start clean again.

To look at a surface that needs rows in it, ask for sample data — the *http (sample data)*
launch profile, or:

```bash
dotnet run --project src/AniQueue.Web -- --SeedSampleData=true
```

It covers completed, watching, planning and hidden titles, several seasons of one series with
the relations between them, an ordered queue and an applied AI ranking. Development only, and
it declines if the library already holds anything. **Do not sync a real account into a seeded
database:** the sample titles carry identifiers AniList does not issue, so the first real list
that comes back without them reports them as no longer on AniList — which is correct, and is
why the sample data leaves AniList sync switched off in the database it creates.

### Opening it from a phone

The listings have a narrow layout, and a browser made narrow is only an approximation of one.
To reach the development server from a device on the same network, use the *http (lan)* launch
profile, or:

```bash
dotnet run --project src/AniQueue.Web --urls http://0.0.0.0:5048
```

The ordinary profiles bind to `localhost`, which nothing else on the network can reach. This one
binds to every interface, so Windows will also want an inbound rule for the port — once, from an
elevated prompt:

```powershell
New-NetFirewallRule -DisplayName "AniQueue dev 5048" -Direction Inbound -Protocol TCP -LocalPort 5048 -Profile Private -Action Allow
```

Then browse to `http://<your machine's LAN address>:5048`. Two things differ from localhost:
`navigator.clipboard` does not exist over plain http, so *Copy the whole prompt* falls back to
`execCommand` and may be refused outright — the text is on the page either way — and the device
has to be on the same network rather than on mobile data.

**Development only.** It is a plain-http server with no authentication, so it is for a network
you trust. Deployment is a container behind whatever the operator puts in front of it.

## Importing a MyAnimeList export

1. On MyAnimeList, go to **List → Export** and download your anime list. The file arrives
   gzipped; extract it so it ends in `.xml`.
2. Open **Sources** in AniQueue and select the file on the MyAnimeList card.
3. Review the preview: how many entries are new, updated, unchanged, in conflict or
   unusable, and exactly which fields would change.
4. Confirm.

Nothing is written until you confirm. Re-importing the same export is a no-op, and an
import never overwrites what you curated here — personal notes, hidden flag, queue position
and recommendation data are all left alone. Entries that cannot be confidently identified are
reported as conflicts and skipped rather than merged.

## Syncing an AniList list

AniList lists are read without authentication, so there is no OAuth and no API key. Open
**Sources**, type the username on the AniList card, and press Save. It takes effect
immediately — no restart. How often to read it, what to do with conflicts and disappearances,
and which source is **primary** are all on the same card.

`Sync:Enabled=false` is the kill switch, and it lives in the settings file rather than only in
the application: the moment it is needed is the moment the UI may not be reachable.

Only your list is read. Nothing is ever written back to AniList or MyAnimeList.

## Ranking your backlog

AniQueue asks a model to predict what **you** would rate each thing you are planning to watch,
against the scores you have already given — so the answer is your taste rather than general
reputation. It writes a score, a confidence and a sentence of reasoning, and it never touches
your Up Next order.

Two ways to get one, on the **Scoring** page. Both send the same request and accept the
same reply; only the carrying differs.

- **By hand.** Build the request, paste it into any model you like, bring the answer back. No
  configuration at all, and it works with a model you have no API access to.
- **A model you host.** Point AniQueue at anything speaking the OpenAI chat-completions API —
  LM Studio, Ollama, llama.cpp — and press **Rank now**. No account and no API key: it runs on
  your own hardware.

Nothing is written until you have seen what it would do. A reply that does not fit the schema is
reported rather than repaired, because a score nobody can account for is exactly what this
feature exists to avoid.

### Letting it run on its own

Turn on a schedule under **Run on a schedule** and AniQueue works through the backlog unattended,
a batch at a time, stopping when there is nothing left that needs doing.

It only ranks what is worth ranking: titles that have never been scored, and titles whose score
has been overtaken by your later ratings. **A ranking goes out of date because you rated more
shows, not because time passed** — so a library you have not touched costs nothing to leave the
schedule switched on for, and the day you finish and rate a few more, it quietly brings the
backlog back up to date.

A run you press takes priority over one running in the background, and the card at the top of the
page says whether there is anything outstanding.

`Scoring:Enabled=false` is the kill switch, for the same reason sync has one.

## Settings

Every setting you can change lives in one file — `userconfig.json`, written beside the database
in your `/data` volume on first run. AniQueue writes it whenever you change something in the
application, and you can edit it by hand, which is how you change its behaviour when its own
pages cannot be reached.

It holds every setting it accepts, one key per line, each with a line saying what it does. What
you see is what AniQueue is doing — there are no hidden defaults to know about. Edit a value,
save, and restart to be certain a hand edit took effect.

AniQueue rewrites the file whole each time a setting changes, so anything else you put in it
will not survive.

Your compose file or Unraid template holds the container's concerns — the `/data` volume and
the published port — and nothing else. There is no second place to look.

Database settings are the exception, and stay outside that file. `Database:Path` could not live
in it even in principle — AniQueue finds the file by looking *beside* the database, so a path set
inside it could not be read until it was already in use. The image already points it at
`/data/aniqueue.db`, so there is nothing to set; `Database__Path` as an environment variable is
there to override that, and almost nobody needs to. The rest are tuning for the storage engine
rather than choices about your library, and their defaults are right unless something is already
wrong.

Preferences about how AniQueue *looks* to you — the language titles are shown in, and later the
theme and date format — are kept in the database instead, so they travel with a copy of your
library rather than cluttering the file you edit when something is wrong.

---

Backup and restore are the volume: stop the container, copy it, start it again. There is no
separate export format, and D33 in the roadmap says why.

## Licence

Not yet chosen.

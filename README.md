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
architectural decisions and acceptance criteria. Nothing here is installable yet.

| Phase | | |
|---|---|---|
| 0 | Foundation — solution, projects, build settings | **complete** |
| 1 | Domain model, EF Core + SQLite, migrations, seed data | **complete** |
| 2 | MyAnimeList XML import → preview → commit → backlog | **complete** |
| 3 | Backlog page — filters, sorting, bulk actions | **complete** |
| 4 | Up Next queue — manual ordering, drag and drop | **complete** |
| 5 | AniList read sync — reconciliation, on demand, then unattended | **complete** |
| 6 | Relations — a title's prequels, sequels and spin-offs | next |
| 7–11 | Dashboard → interchange → AI ranking → artwork → Docker | planned |

## Documentation

| Document | Purpose |
|---|---|
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | Authoritative plan: architecture, domain model, phases, decisions |
| [`docs/BUILD-PROMPT.md`](docs/BUILD-PROMPT.md) | Original project brief, preserved for reference |

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

## Importing a MyAnimeList export

1. On MyAnimeList, go to **List → Export** and download your anime list. The file arrives
   gzipped; extract it so it ends in `.xml`.
2. Open **Import** in AniQueue and select the file.
3. Review the preview: how many entries are new, updated, unchanged, in conflict or
   unusable, and exactly which fields would change.
4. Confirm.

Nothing is written until you confirm. Re-importing the same export is a no-op, and an
import never overwrites what you curated here — personal notes, hidden flag, queue position
and recommendation data are all left alone. Entries that cannot be confidently identified are
reported as conflicts and skipped rather than merged.

## Syncing an AniList list

AniList lists are read without authentication, so there is no OAuth and no API key. Two
settings, in two places, deliberately:

- **Which account to read** is operator configuration. Set `Sync:AniList:UserName` — as an
  environment variable (`Sync__AniList__UserName`), in `appsettings.json`, or by uncommenting
  the line in the `userconfig.json` written beside the database on first run.
- **How often, and what to do with conflicts and disappearances** are your settings, on the
  **Sources** page.

`Sync:Enabled=false` is the kill switch. It is a configuration key rather than a toggle in the
application because the moment it is needed is the moment the UI cannot be reached.

Only your list is read. Nothing is ever written back to AniList or MyAnimeList.

---

Installation, Docker deployment, backup/restore and the AI ranking workflow will be documented
here as the corresponding phases land.

## Licence

Not yet chosen.

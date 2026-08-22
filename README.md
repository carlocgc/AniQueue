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
| 6 | Relations — a title's prequels, sequels and spin-offs | **complete** |
| 7 | Scoring interchange — export the backlog, bring a ranking back | **complete** |
| 8 | Hosted model scoring — settings store, endpoint, surface, scheduled sweep | **complete** |
| 9–14 | Artwork → settings → Docker → auth → CI → security pass | planned |

## Documentation

| Document | Purpose |
|---|---|
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | Authoritative plan: architecture, domain model, phases, decisions |
| [`docs/BUILD-PROMPT.md`](docs/BUILD-PROMPT.md) | Original project brief, preserved for reference |
| [`CLAUDE.md`](CLAUDE.md) | Working conventions: the development database, verification, testing, platform gotchas |

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

Two ways to get one, on the **Recommendations** page. Both send the same request and accept the
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

Database settings are the exception, and stay in the environment. `Database:Path` could not live
in the file even in principle — AniQueue finds the file by looking *beside* the database, so a
path set inside it could not be read until it was already in use. The rest are tuning for the
storage engine rather than choices about your library, and their defaults are right unless
something is already wrong.

Preferences about how AniQueue *looks* to you — the language titles are shown in, and later the
theme and date format — are kept in the database instead, so they travel with a copy of your
library rather than cluttering the file you edit when something is wrong.

---

Installation, Docker deployment, backup/restore and the AI ranking workflow will be documented
here as the corresponding phases land.

## Licence

Not yet chosen.

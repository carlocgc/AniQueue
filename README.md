# AniQueue

Self-hosted anime watchlist and **backlog decision layer**.

AniQueue is not a MyAnimeList or AniList replacement. It assumes your library already lives
somewhere and answers the question those tools answer badly: **what do I actually watch
next?**

- Import your existing library (MyAnimeList XML export, or AniQueue JSON).
- Group franchise seasons, films, OVAs and specials into a single backlog decision.
- Maintain a deliberate, hand-ordered **Up Next** queue.
- Rank the backlog against **your own historical scores**, not global popularity — using an
  external LLM of your choice, with **no API key required**.
- Run it on your own hardware. No cloud dependency.

## Status

**In development — pre-MVP.** See [`docs/ROADMAP.md`](docs/ROADMAP.md) for the phase plan,
architectural decisions and acceptance criteria. Nothing here is installable yet.

| Phase | | |
|---|---|---|
| 0 | Foundation | in progress |
| 1–11 | Domain → Docker | planned |

## Screenshots

_To be added once the UI exists._

## Documentation

| Document | Purpose |
|---|---|
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | Authoritative plan: architecture, domain model, phases, decisions |
| [`docs/BUILD-PROMPT.md`](docs/BUILD-PROMPT.md) | Original project brief, preserved for reference |

## Technology

.NET 10 · ASP.NET Core Blazor Web App (Interactive Server) · EF Core 10 · SQLite · xUnit ·
Docker. No React, Angular, Vue, or separate frontend build system.

## Development

Requires the .NET 10 SDK. Visual Studio 2026 or `dotnet` CLI.

```bash
dotnet restore
dotnet build
dotnet test
```

Installation, configuration, Docker deployment, backup/restore, MAL import and the AI
ranking workflow will be documented here as the corresponding phases land.

## Licence

Not yet chosen.

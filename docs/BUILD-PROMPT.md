# Project: AniQueue

Build a production-quality MVP for a self-hosted anime watchlist and backlog management application provisionally named **AniQueue**.

The purpose of AniQueue is not to replace MyAnimeList or AniList as an anime database/community. It is a **decision and backlog-management layer** that helps a user decide what to watch next.

The core problems it should solve are:

- MAL/AniList Plan-to-Watch lists become very large and unordered.
- Users cannot easily maintain a deliberate "watch this next" queue.
- Franchise seasons, OVAs, films and specials clutter the backlog as separate choices.
- Users may want to prioritise titles based on their own historical ratings rather than global popularity.
- Existing trackers are good at recording what someone watched but weaker at helping decide **what to watch next**.
- A user should be able to self-host the application without depending on a cloud service.

Implement a working application, not merely scaffolding or a design document.

---

# 1. Technology

Use:

- **.NET 10**
- **ASP.NET Core Blazor Web App**
- Interactive Server rendering where interactivity is required
- C#
- Entity Framework Core 10
- SQLite
- ASP.NET Core dependency injection, configuration and logging
- xUnit for automated tests
- Docker
- Docker Compose

Prefer framework-native functionality over unnecessary third-party dependencies.

A small JavaScript dependency is acceptable where the browser has functionality that would otherwise be awkward in Blazor. In particular, using something lightweight such as SortableJS through minimal JS interop for high-quality drag-and-drop ordering is acceptable.

Do not introduce React, Angular, Vue, Node.js or a separate frontend build system.

Keep this a conventional .NET solution that another .NET developer can understand easily.

---

# 2. Deployment model

The primary deployment target is a small self-hosted server such as:

- Unraid
- Docker Compose host
- NAS
- home server
- Linux VM

For the MVP use **one application container with SQLite** rather than requiring a separate database container.

Persist all mutable data under:

`/data`

For example:

`/data/aniqueue.db`

The Docker Compose configuration must mount `/data` as a named volume or bind-mountable directory.

The application must not lose data when the container is recreated or upgraded.

Expose the web application on a configurable port, defaulting to:

`8080`

Provide:

- `Dockerfile`
- `docker-compose.yml`
- `.dockerignore`
- health check endpoint
- health check configuration in Docker Compose
- environment-variable based configuration
- sensible production logging
- graceful startup failure if the database cannot be accessed

Run the final container as a non-root user where practical.

The README must contain examples for both:

```bash
docker compose up -d
```

and a direct `docker run` invocation.

---

# 3. Application scope

This is initially a **single-user personal application**.

Do not add account registration, OAuth login or a complicated authentication system in the first MVP.

However, avoid domain decisions that would make adding users/profiles impossible later.

Create a default local profile and associate library data with a `ProfileId` where appropriate.

The UI should be responsive and work well on:

- desktop
- tablet
- mobile browser

The visual style should be clean, modern and information-dense without looking like an enterprise admin panel.

---

# 4. Core domain concepts

Model at least the following concepts.

## Anime

Represents an individual anime entry.

Suggested fields:

- Id
- Title
- AlternativeTitle
- MediaType
- EpisodeCount
- EpisodeDurationMinutes if known
- ReleaseYear if known
- CoverImageUrl if known
- Description if known
- Source
- SourceAnimeId
- FranchiseId nullable
- FranchiseOrder nullable
- CreatedAt
- UpdatedAt

`Source` should support at least:

- Manual
- MyAnimeList
- AniList

Do not couple domain entities directly to MAL or AniList DTOs.

---

## LibraryEntry

Represents the user's relationship with an anime.

Fields should include:

- Id
- ProfileId
- AnimeId
- Status
- UserScore
- EpisodesWatched
- DateStarted
- DateCompleted
- DateAdded
- LastUpdated
- PersonalNotes
- ManualPriority
- QueuePosition nullable
- IsHidden
- RecommendationScore nullable
- RecommendationConfidence nullable
- RecommendationReason nullable
- RecommendationUpdatedAt nullable

Statuses should include:

- Planning
- Watching
- Completed
- OnHold
- Dropped

Use an enum internally.

---

## Franchise

Represents multiple anime entries that the user wants to treat as one backlog decision.

Example:

`Fate/kaleid liner Prisma Illya`

might contain:

- Season 1
- 2wei
- 2wei Herz
- 3rei
- films
- OVAs
- specials

Suggested fields:

- Id
- Name
- Description
- ManualSortOrder

An Anime may belong to zero or one franchise for the MVP.

The user must be able to manually:

- create a franchise
- rename it
- add/remove titles
- reorder entries inside it
- dissolve it

Do not attempt automatic franchise detection in the first version unless it can be implemented cleanly as an optional suggestion.

---

# 5. Main backlog concepts

The application should distinguish between:

## Backlog

All anime/franchises that the user intends to watch.

## Up Next queue

A manually ordered subset of the backlog.

For example:

1. Gunbuster
2. Nichijou
3. Najica Blitz Tactics
4. New Game!
5. Hinamatsuri

The queue must support true **manual drag-and-drop ordering**.

Moving an item must immediately persist its new position.

Provide buttons for:

- Move to top
- Move up
- Move down
- Move to bottom
- Remove from queue

so ordering remains usable without drag-and-drop.

Allow anime OR an entire franchise to appear as an item in the queue.

Avoid representing every sequel season as an independent high-level choice when the user has grouped them into a franchise.

---

# 6. Dashboard

Create a useful home dashboard.

It should show at least:

## Currently Watching

Anime being watched with progress such as:

`8 / 13 episodes`

and a progress bar.

Allow quick `+1 episode`.

---

## Up Next

Show approximately the first 5–10 queue entries.

Provide a prominent:

**Start Watching**

action.

Starting an anime should:

- change status to Watching
- set start date if absent
- remove it from Up Next as appropriate

For franchise entries, starting the franchise should select the next unfinished anime according to franchise order.

---

## Backlog summary

Display counts such as:

- Total backlog
- Franchises
- Standalone anime
- Estimated backlog runtime
- Completed
- Watching

---

## Suggested Next

If recommendation results exist, show several high-ranked backlog items that are not already at the top of the manual queue.

AI recommendations must never silently override the user's manual ordering.

---

# 7. Backlog page

Create a dedicated backlog management view.

Support:

- search
- filtering
- sorting
- drag-and-drop into Up Next
- bulk selection
- bulk queue addition
- bulk priority change
- bulk hide/remove where appropriate

Useful filters should include:

- status
- franchise / standalone
- media type
- decade
- estimated runtime
- score/recommendation score
- source
- priority

Provide quick filters such as:

- Under 2 hours
- Under 6 hours
- Movie
- OVA
- TV
- 80s
- 90s
- 2000s
- 2010s
- 2020s
- High AI confidence
- Not yet ranked

Only show a filter when the necessary metadata is available.

---

# 8. "What should I watch?" mode

Create a lightweight decision screen for situations where the user wants something now.

Examples:

- "Anything"
- "Something short"
- "A movie"
- "One evening"
- "Old-school anime"
- "From my top 20"
- "Surprise me"

The system should return one or several choices from the user's backlog.

Selection should respect manual priority and, where available, recommendation scores.

For "Surprise me", use weighted randomness rather than always choosing the numerically highest-ranked title.

Do not build a conversational AI interface for this in v1.

---

# 9. Runtime estimates

Where episode count and duration are known, calculate:

`EstimatedRuntimeMinutes`

For example:

`12 × 24 min = 288 minutes`

A franchise runtime is the sum of its unfinished entries.

Display human-readable durations such as:

- 1h 45m
- 4h 48m
- 22h

Do not invent runtime data when it is unavailable.

---

# 10. Import architecture

Create an import pipeline with clear separation between:

1. parsing
2. normalisation
3. validation
4. matching/deduplication
5. preview
6. committing changes

Never immediately mutate the database when the user uploads an import.

Show an **Import Preview** first.

The preview should summarise:

- new titles
- existing titles that will be updated
- skipped entries
- conflicts
- invalid records
- total completed
- total planning
- total watching

The user then explicitly confirms the import.

Imports should be idempotent where reasonable.

---

# 11. MyAnimeList XML import

Implement support for the standard MAL XML export format.

The MAL export contains entries similar to:

```xml
<anime>
    <series_animedb_id>268</series_animedb_id>
    <series_title><![CDATA[Golden Boy]]></series_title>
    <series_type>OVA</series_type>
    <series_episodes>6</series_episodes>
    <my_watched_episodes>6</my_watched_episodes>
    <my_score>9</my_score>
    <my_status>Completed</my_status>
</anime>
```

Import at least:

- MAL anime ID
- title
- media type
- episode count
- watched episodes
- score
- status
- start date
- finish date
- times watched where useful

Map MAL statuses into internal statuses.

Treat `0000-00-00` as no date.

Do not assume every XML value is valid.

Use secure XML parsing settings and do not permit external entity resolution.

---

# 12. JSON import

Also define a simple **AniQueue interchange JSON format**.

Example:

```json
{
  "version": 1,
  "profile": {
    "name": "Default"
  },
  "anime": [
    {
      "source": "MyAnimeList",
      "sourceAnimeId": "268",
      "title": "Golden Boy",
      "mediaType": "OVA",
      "episodeCount": 6,
      "status": "Completed",
      "episodesWatched": 6,
      "score": 9
    }
  ]
}
```

Implement:

- JSON import
- JSON export
- schema/version field
- validation
- backwards-compatible design

Exporting the entire AniQueue library should provide a practical backup/migration mechanism.

Do not include secrets in an export.

---

# 13. AniList support

Do not require live AniList API integration for the first MVP.

Design an interface such as:

```csharp
public interface IAnimeListProvider
{
    string Name { get; }

    Task<ImportPreview> ImportAsync(
        Stream input,
        CancellationToken cancellationToken);
}
```

or a better equivalent.

Implement:

- MAL XML provider
- AniQueue JSON provider

Leave AniList API/import as an obvious extension point.

Do not create fake AniList integration just to satisfy this requirement.

---

# 14. AI recommendation feature

This is an important feature.

The objective is:

> Rank the user's unwatched backlog based primarily on what that specific user has scored highly or poorly in the past.

For example, a user who scores:

- Golden Boy — 9
- Dragon Maid — 9
- Konosuba — 9
- AIKa — 10
- Gunbuster-style OVAs — highly

should receive different recommendations from a generic MAL popularity ranking.

AI recommendations should be personalised from the user's own historical ratings.

However, **the MVP must not require an AI API key**.

Implement AI support in layers.

---

# 15. AI recommendation workflow — Phase 1: export/import

Implement a **Recommendation Request Export**.

The application should create a compact JSON document suitable for giving to an external LLM.

Example structure:

```json
{
  "schemaVersion": 1,
  "type": "anime-ranking-request",
  "profile": {
    "scoringScale": {
      "minimum": 1,
      "maximum": 10
    }
  },
  "completed": [
    {
      "title": "Golden Boy",
      "score": 9,
      "sourceAnimeId": "268"
    }
  ],
  "candidates": [
    {
      "candidateId": "internal-stable-id",
      "title": "Gunbuster"
    },
    {
      "candidateId": "internal-stable-id",
      "title": "Nichijou"
    }
  ]
}
```

Only export information required for recommendations.

Do NOT include:

- email addresses
- passwords
- API keys
- IP addresses
- server information
- personal notes unless the user explicitly opts in

The UI should explain what information is being exported.

Provide:

- Download JSON
- Copy JSON

---

# 16. AI prompt generation

Alongside the JSON, generate a ready-to-copy LLM instruction.

The prompt should tell the model to:

- analyse the user's completed scores
- infer taste patterns
- consider both positive and negative ratings
- rank every supplied candidate
- avoid assuming popularity equals suitability
- avoid omitting candidates
- return JSON only
- preserve each candidateId exactly

The result schema should be something like:

```json
{
  "schemaVersion": 1,
  "type": "anime-ranking-result",
  "rankings": [
    {
      "candidateId": "internal-stable-id",
      "rank": 1,
      "predictedScore": 9.1,
      "confidence": 0.88,
      "reason": "Strong match for the user's preference for..."
    }
  ]
}
```

Validate:

- candidate IDs
- duplicates
- missing candidates
- rank collisions
- numeric ranges
- unexpected candidates

Never execute arbitrary content returned by an AI.

Treat AI responses as untrusted data.

---

# 17. AI ranking import

Create an **Import AI Ranking** screen.

Allow:

- upload JSON file
- paste JSON into a text area

Preview the imported ranking before applying it.

Show:

- title
- proposed rank
- predicted score
- confidence
- reason

On confirmation, save recommendation data to LibraryEntry.

Important:

**AI ordering must remain separate from manual ordering.**

The user should be able to choose:

- Manual order
- AI recommended order
- Hybrid order

but importing AI recommendations must NEVER silently rearrange the user's manually curated Up Next queue.

---

# 18. Hybrid ranking

Implement a simple configurable hybrid ranking algorithm.

Inputs might include:

- manual priority
- AI predicted score
- AI confidence
- age/date added
- whether title is already in Up Next
- runtime preference

Keep the formula simple and transparent.

Show the user why an item is ranked where it is.

Avoid a black-box internal scoring algorithm.

---

# 19. Future AI provider architecture

Define an abstraction such as:

```csharp
public interface IAiRecommendationProvider
{
    Task<RecommendationResult> RankAsync(
        RecommendationRequest request,
        CancellationToken cancellationToken);
}
```

The initial implementation may be:

`ManualJsonRecommendationProvider`

Do not require a network-connected AI provider in the MVP.

Structure the project so future providers could include:

- OpenAI API
- another commercial LLM API
- Ollama
- LM Studio
- generic OpenAI-compatible endpoint

Any future API secrets must:

- come from environment variables or another server-side secret mechanism
- never be sent to the browser
- never be written to normal application logs
- never be included in exports

Do not implement provider-specific secret storage in the database for the MVP.

---

# 20. Recommendation history

Keep a small history of recommendation runs.

Store:

- timestamp
- provider
- number of completed titles used
- number of candidates
- result count
- optional model identifier
- whether it was applied

Allow the user to compare the current recommendation set to a previous one.

Do not store the entire request repeatedly if that would unnecessarily duplicate data.

---

# 21. Franchise behaviour

Franchises are central to the application.

A collapsed franchise card should show something such as:

**Slayers**
- 0 / 5 main entries watched
- ~52h remaining
- First: Slayers (1995)
- AI score: 8.4
- Queue position: #7

Expanding it should show its entries in viewing order.

The user can decide whether specials and OVAs count toward the franchise's main completion.

Support a boolean such as:

`OptionalWithinFranchise`

An optional special should not prevent a franchise being considered substantially completed.

---

# 22. Starting and completing anime

Provide quick actions.

When a Planning anime starts:

- status becomes Watching
- DateStarted defaults to current date
- keep episode count/progress

When an anime reaches its known final episode:

Offer:

**Mark Completed**

Do not automatically assign a score.

When completing an anime, optionally prompt for a 1–10 score.

If it belongs to a franchise, show:

**Next in franchise: ...**

and allow adding that entry to Up Next.

---

# 23. Manual entries

Allow users to create anime manually.

Required:

- title

Optional:

- media type
- episodes
- duration
- year
- source URL
- notes

A manual entry can later be matched to MAL or AniList without losing:

- queue position
- notes
- history
- franchise membership

---

# 24. Deduplication

When importing, primarily match on:

`Source + SourceAnimeId`

If unavailable, cautiously attempt title matching.

Do not silently merge ambiguous title matches.

Show conflicts in Import Preview.

Preserve the user's local manual fields when refreshing imported source data.

For example, an import must not overwrite:

- manual queue position
- personal notes
- franchise grouping
- hidden flag
- recommendation history

unless explicitly requested.

---

# 25. Settings

Create a Settings screen with at least:

## General

- display name
- default queue size
- date format
- theme: System / Light / Dark

## Backlog

- show optional franchise entries
- default sort
- default filters

## Recommendations

- default recommendation mode
- AI export privacy options
- recommendation weighting

## Data

- export backup
- import backup
- clear recommendation results

Dangerous destructive actions must require explicit confirmation.

---

# 26. UI/navigation

Primary navigation:

- Dashboard
- Up Next
- Backlog
- Watching
- Franchises
- Recommendations
- Import / Export
- Settings

The most important workflows should require few clicks.

Do not create an excessively nested administration UI.

---

# 27. Anime cards

A typical anime card/list row should support showing:

- cover image if available
- title
- year
- media type
- episodes
- runtime
- status
- user's score if completed
- recommendation score if available
- manual priority
- queue position
- franchise

Missing metadata should degrade cleanly rather than displaying lots of "N/A" fields.

---

# 28. Images

Do not store remote image binaries in SQLite.

If cover URLs exist, initially use the URL.

The application must still work when images are unavailable.

Consider adding a generic placeholder cover.

Design image handling behind a service so local caching can be added later.

---

# 29. Database design

Use EF Core migrations.

Create appropriate indexes, especially for:

- ProfileId
- Anime Source + SourceAnimeId
- Status
- QueuePosition
- FranchiseId

Enforce uniqueness where sensible.

Do not use `EnsureCreated()` as a permanent replacement for migrations.

On normal Docker startup, safely apply pending migrations.

Log migration failures clearly.

---

# 30. Queue ordering implementation

Queue ordering must remain stable.

Do not use floating point positions.

A straightforward integer position is acceptable.

When reordering:

- perform updates transactionally
- normalise positions
- avoid duplicates
- ensure positions remain contiguous

Create tests for reorder edge cases.

---

# 31. Security

Even though this is a home-server application, follow normal web security practices.

At minimum:

- anti-forgery protection where applicable
- server-side validation
- safe file upload limits
- reject unexpectedly large imports
- secure XML parsing
- HTML encode user content
- no arbitrary file paths supplied by users
- no command execution
- no evaluation of AI content
- do not expose stack traces in production
- secrets via environment/configuration, not source control

Assume the application might eventually sit behind a reverse proxy.

Respect forwarded headers only when explicitly configured.

Do not make assumptions that every request comes from localhost.

---

# 32. Logging

Use structured `ILogger` logging.

Useful events:

- application startup
- migration
- import started
- import preview generated
- import committed
- recommendation request exported
- recommendation result imported
- queue changed

Do not log entire uploaded files or AI payloads by default.

Do not log secrets.

---

# 33. Tests

Create automated tests for important domain behaviour.

At minimum test:

- MAL XML parsing
- malformed MAL XML
- `0000-00-00`
- JSON import
- duplicate detection
- import idempotency
- queue reorder
- franchise ordering
- runtime calculations
- completion transitions
- recommendation JSON validation
- AI candidate ID validation
- missing candidate handling
- invalid predicted score/confidence
- hybrid ranking

Prefer testing domain/application services without requiring a browser.

Add a small number of integration tests covering EF Core + SQLite.

---

# 34. Sample data

Provide optional development seed data.

Do not seed production automatically.

Include enough fake/sample entries to demonstrate:

- completed anime with different scores
- planning titles
- watching title
- franchise
- Up Next queue
- AI recommendation result

Do not make the project's tests dependent on live external APIs.

---

# 35. README

Write a useful README containing:

- project purpose
- screenshots section placeholder
- architecture overview
- requirements
- Docker installation
- Docker Compose example
- persistent volume explanation
- configuration
- upgrades
- database migrations
- backup
- restore
- MAL XML import instructions
- JSON import/export
- AI ranking workflow
- development setup
- tests
- roadmap

Explicitly explain that v1 AI recommendation can work without giving AniQueue an API key.

---

# 36. Suggested solution structure

Use a clean structure without unnecessary architecture astronautics.

Something approximately like:

```text
AniQueue.sln

src/
  AniQueue.Web/
  AniQueue.Core/
  AniQueue.Infrastructure/

tests/
  AniQueue.Core.Tests/
  AniQueue.Infrastructure.Tests/
```

Responsibilities:

## AniQueue.Core

- domain entities
- enums
- interfaces
- recommendation models
- domain/application services

No dependency on EF Core or Blazor if avoidable.

## AniQueue.Infrastructure

- EF Core
- SQLite
- migrations
- MAL parser
- JSON import/export
- repository/data services
- recommendation persistence

## AniQueue.Web

- Blazor components/pages
- DI composition
- configuration
- Docker host
- web-specific services

Do not add separate projects merely to satisfy a theoretical Clean Architecture diagram.

If a simpler structure proves materially better, document the reasoning before changing it.

---

# 37. API boundaries

Even though the MVP is primarily a Blazor application, keep domain operations behind services rather than writing substantial business logic directly inside UI components.

Examples:

- `IQueueService`
- `ILibraryService`
- `IFranchiseService`
- `IImportService`
- `IRecommendationService`

Do not introduce an HTTP REST API between the Blazor server UI and the same server merely for architectural ceremony.

A public API can be added later.

---

# 38. Performance expectations

The application should easily handle libraries of several thousand anime.

Avoid loading the entire database into memory for ordinary pages.

Use:

- pagination or virtualisation where useful
- async EF queries
- `AsNoTracking` for read-only queries
- indexes
- server-side filtering

An AI export involving the entire completed history and backlog may load the relevant records intentionally.

---

# 39. Accessibility

Use semantic HTML.

Ensure:

- keyboard-accessible controls
- meaningful labels
- reasonable focus behaviour
- buttons are actual buttons
- drag-and-drop has non-drag alternatives
- adequate contrast in light and dark themes

---

# 40. MVP acceptance criteria

The MVP is complete when I can:

1. Run `docker compose up -d`.
2. Open AniQueue in a browser.
3. Upload a MAL XML export.
4. Preview the changes.
5. Confirm import.
6. See Completed, Watching and Planning entries.
7. See my historical scores.
8. Create and edit franchises.
9. Collapse sequel entries into franchises.
10. Add standalone anime or franchises to Up Next.
11. Drag Up Next into an exact manual order.
12. Persist that order across container restarts.
13. Track watching progress.
14. Complete an anime and assign a score.
15. Filter the backlog by useful criteria.
16. Export an AI recommendation request JSON.
17. Copy/download the associated AI prompt.
18. Give it to an external AI.
19. Paste/import the returned ranking JSON.
20. Preview and apply the ranking.
21. View AI recommendations alongside manual priority.
22. Keep manual Up Next order unchanged after AI import.
23. Export the complete AniQueue library as JSON.
24. Restore it from that JSON.
25. Recreate the Docker container without losing the database.

---

# 41. Features explicitly NOT required for initial MVP

Do not let these delay the initial working version:

- MAL OAuth
- AniList OAuth
- live two-way MAL sync
- live two-way AniList sync
- built-in OpenAI API calls
- local Ollama integration
- user registration
- social features
- comments/reviews
- public profiles
- recommendation community
- mobile native apps
- automatic metadata scraping
- automatic franchise detection
- automatic torrent/streaming integrations

Design appropriate extension points where useful, but do not implement speculative infrastructure.

---

# 42. Post-MVP roadmap

Document but do not initially implement:

## Phase 2

- AniList GraphQL import/sync
- metadata enrichment
- cover art
- genres/tags/studios
- automatic franchise suggestions

## Phase 3

- optional AI API providers
- OpenAI-compatible endpoint
- Ollama / LM Studio
- encrypted/secrets-based configuration
- scheduled re-ranking

## Phase 4

- MAL API sync
- AniList write-back
- score/status/progress sync
- conflict resolution

## Phase 5

- multi-user support
- authentication
- household profiles
- recommendation comparison between profiles

---

# 43. Implementation approach

Work incrementally.

First inspect the repository.

If it is empty:

1. Create the solution/projects.
2. Establish the domain model.
3. Implement EF Core + SQLite.
4. Create migrations.
5. Implement MAL XML import.
6. Build import preview/commit.
7. Build backlog UI.
8. Build Up Next ordering.
9. Build franchise management.
10. Build watching/progress workflow.
11. Implement JSON backup/import.
12. Implement AI request/result JSON.
13. Build recommendation UI.
14. Add Docker deployment.
15. Add tests.
16. Finish README.

At every stage keep the application buildable.

Run:

```bash
dotnet restore
dotnet build
dotnet test
```

regularly.

Before declaring the task complete:

- build the Release configuration
- run all tests
- build the Docker image
- start the Docker Compose stack
- verify the health endpoint
- verify SQLite persists across container recreation

Fix failures rather than merely documenting them.

---

# 44. Coding standards

Use modern idiomatic C#.

Enable:

```xml
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
```

Prefer:

- records for immutable DTOs where appropriate
- cancellation tokens
- async APIs
- dependency injection
- options pattern for configuration
- clear result/error types
- small focused components
- explicit validation

Avoid:

- service locator
- global mutable state
- static database access
- giant page components
- giant "Manager" classes
- generic repositories that merely wrap every EF Core method
- unnecessary MediatR/CQRS abstractions
- premature microservices
- JavaScript-heavy SPA architecture

Use comments to explain **why**, not obvious syntax.

---

# 45. First deliverable

Start by implementing the complete vertical slice required to:

**MAL XML → Import Preview → SQLite → Backlog page → manually ordered Up Next queue**

Once that works end-to-end, continue through the remaining MVP requirements.

Do not stop after generating project scaffolding.

When finished, provide:

1. A concise summary of the implemented architecture.
2. The repository structure.
3. Exact Docker commands to run it.
4. Any configuration variables.
5. How to import a MAL export.
6. How to perform the manual JSON-based AI recommendation workflow.
7. Test results.
8. Known MVP limitations.
9. Recommended next development task.
# Working in this repository

How to work here. **What** to build and **why** lives in [`docs/ROADMAP.md`](docs/ROADMAP.md),
which is authoritative — every design decision has a `D`-number, and changing one means editing
that file in the same PR that changes the code (§12). This file is the practical half: the
things that have cost time to learn.

## Do not delete the development database

`src/AniQueue.Web/data/aniqueue.db` holds a real synced library — hundreds of titles, a queue,
applied rankings. **Never `rm -rf src/AniQueue.Web/data`**, and do not clear it "to start clean".
Re-creating that state by hand costs far more than the tidiness is worth, and the directory is
git-ignored so it never needs cleaning for hygiene.

Clear it only when the empty-database path is the thing under test — first boot, `userconfig.json`
generation, the empty-library screen (D27) — and say so when doing it.

## Sample data

For a surface that needs rows, use the sample profile rather than touching the real database:

```bash
dotnet run --project src/AniQueue.Web --launch-profile "http (sample data)"
```

Everything it touches lives under `src/AniQueue.Web/data/sample/` — its own `sample.db` **and its
own `userconfig.json`**. Deleting that whole directory is fine and it rebuilds on the next run.

**The trap here used to be that both profiles shared one `data/userconfig.json`**, so saving a
setting during a sample run wrote to the real configuration, and testing anything that saves meant
backing the file up first. Phase 10a closed it by moving the sample profile into its own
directory, which it had to: that phase moved the per-source sync settings into the file, so the
seeder could no longer switch AniList off in the sample database and would have switched it off
for real instead.

Two consequences worth knowing. The sample run has **no AniList account**, because it no longer
inherits one — so *Sync now* has nothing to read, which is the intended state (D27) rather than a
setup step somebody forgot. And a settings change made during a sample run does **not** show up in
the real application, which is what you want when testing and confusing for about ten seconds the
first time it happens.

## Verify by running it, not by compiling it

Every phase of this project has had bugs that only appeared when the application ran: a circuit
crash from two routes sharing one field, a duplicate *Discard* button, a size estimate that never
refreshed, a request too large for a real model's context. None were visible in a clean build or
a green suite.

`.claude/launch.json` defines two preview configurations — `aniqueue-web` (real database) and
`aniqueue-sample`. Drive the page, read the state back, and check the server log. Prefer
`read_page` and `javascript_tool` over screenshots: the browser pane is often not displayed, and
the accessibility tree is better evidence anyway.

**End-to-end against a real model is possible.** The user runs LM Studio on their LAN, reachable
from this machine, and testing against it has found bugs a stub could not — a rejected
`response_format`, a context-size refusal, a real ranking applied. Ask before assuming it is
switched on.

## Build gotchas

**File locks.** If the user has the app running in Visual Studio, building `AniQueue.Web` fails
at the *copy* step (`MSB3021`/`MSB3027`) while compilation has already succeeded. Those are not
compile errors — check for `error CS` before believing the count. Ask the user to stop it rather
than killing `AniQueue.Web`; it may be their debug session. `preview_stop` sometimes leaves an
orphan, and one whose start time matches a preview you began is yours to clean up.

**EF scaffolding writes CRLF**, including the model snapshot it rewrites every time. §12 requires
LF everywhere, so strip it after `dotnet ef migrations add`:

```bash
git ls-files -mo --exclude-standard | while read -r f; do [ -f "$f" ] && tr -d '\r' < "$f" > "$f.tmp" && mv "$f.tmp" "$f"; done
```

## SQLite constraints that have bitten twice

**SQLite can neither `ORDER BY` nor compare a `DateTimeOffset`.** EF will not translate either,
and it fails at run time rather than compile time.

- To order by recency, use a `DateOnly` column plus `Id` as a tiebreak, as `BuildRequestAsync`
  does for history.
- Where there is no such stand-in, project the timestamps and decide in memory over a bounded
  set, as `GetCoverageAsync` does.

Enum columns are stored as integers, so renaming an enum *type* is a pure code change, but
reordering or removing its *values* is a data contract break.

## Testing

- **`AniQueue.Core.Tests`** — no database, no fixtures, milliseconds. Core references nothing on
  purpose; keep it that way.
- **`AniQueue.Infrastructure.Tests`** — real EF Core against real SQLite via `SqliteTestDatabase`.
  The EF InMemory provider is deliberately unused: it does not enforce the constraints these
  tests exist to check.
- **There are no component tests.** No bUnit, and adding it is a dependency decision (§12) rather
  than a testing one. Pages are verified by running them.

Conventions that have earned their place:

- **A stub `HttpMessageHandler` must observe its cancellation token.** One that does not lets a
  cancelled run appear to succeed, hiding the behaviour its test exists to check.
- **Prefer a fake over a mock library.** The job and endpoint tests use hand-written fakes that
  record what they were asked; there is no mocking package and none is needed.
- **Test against the real component where its behaviour is the contract.** The settings tests
  build a real `ConfigurationBuilder` chain with the real JSON provider, because what that
  provider accepts is what `userconfig.json` is written against.
- **Name a test after the behaviour, not the method.** `A_score_goes_stale_once_enough_further_titles_have_been_rated`,
  not `GetCoverageAsync_Returns_Stale`.
- **Do not leave a test that cannot fail.** If the setup makes the assertion trivially true,
  delete it or fix the setup.

## Dependencies

No new third-party package without explicit approval (§12), and versions are centrally managed in
`Directory.Packages.props`.

Before reaching for one, check whether the ASP.NET Core shared framework already has it — a
`FrameworkReference` costs nothing and needs no approval:

```xml
<FrameworkReference Include="Microsoft.AspNetCore.App" />
```

That is how the test project gets `Microsoft.Extensions.Configuration.Json`.

## Branches, commits and comments

- One branch per phase part: `feature/phase-N<letter>-slug` → PR into `development`. `main` is
  release-only. Delete branches once merged.
- **Comments explain why, not what**, and cite their `D`-number. When a decision is reversed,
  replace the old comment with the reason it lost rather than deleting it — the argument that
  failed is worth leaving legible.
- Commit messages and PR bodies are prose, not bullet dumps: what changed, what it cost, what was
  found while doing it. Say plainly what could not be verified.

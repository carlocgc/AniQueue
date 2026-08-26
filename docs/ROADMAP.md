# AniQueue — Roadmap

**Status:** authoritative planning document. Supersedes nothing; amended by PR.

AniQueue is a self-hosted anime watchlist and **backlog decision layer**. It is not a
MyAnimeList/AniList replacement — it assumes your library already exists somewhere and
answers the question those tools answer badly: *what do I actually watch next?*

Put precisely: **AniQueue owns the order of your watch list; the service you already use
owns its membership** (D11). The order is maintained jointly by you and an AI, and it
persists.

Problems in scope:

- Plan-to-Watch lists grow large and unordered.
- There is no deliberate, hand-curated "watch this next" queue.
- Franchise seasons, OVAs, films and specials arrive in the backlog with nothing saying they
  are related. *Amended by D24: the answer is to surface each title's relations on its own
  row rather than to collapse them into one, so this reads as a missing-context problem
  rather than a clutter problem.*
- Prioritisation should follow *your* historical scores, not global popularity.
- It must run on your own hardware with no cloud dependency.

### Source documents

| Document | Role |
|---|---|
| [`BUILD-PROMPT.md`](BUILD-PROMPT.md) | Original brief, preserved verbatim. Historical reference. |
| `ROADMAP.md` (this file) | **Authoritative.** Brief + agreed deviations + phase plan. |

Where this file and the build prompt disagree, this file wins, and §2 records why.

---

## 0. Verified environment

Checked on the development machine, not assumed:

| Component | Version |
|---|---|
| .NET SDK | 10.0.301 (runtime host 10.0.9) |
| Visual Studio | Community 2026 — 18.7.11925.98 |
| Docker Engine / Compose | 29.5.3 / v5.1.4 |
| git / gh | 2.54.0.windows.1 / 2.92.0 |
| EF Core SQLite (latest) | 10.0.11 |

Two findings that contradict reasonable assumptions:

- `dotnet new xunit` on .NET 10 emits **xUnit v2.9.3**, not v3. We use v2 as templated.
- No .NET workloads installed, and none are needed — plain `net10.0` + `Microsoft.NET.Sdk.Web`.

**The repository is public.** No secret, token, API key, or personal export may ever be
committed. Sample/seed data must be obviously fictional.

---

## 1. Technology

Fixed by the brief and agreed:

- .NET 10, C#, ASP.NET Core **Blazor Web App** with Interactive Server rendering
- EF Core 10 + SQLite
- Built-in DI, configuration, logging, options pattern
- xUnit, Docker, Docker Compose

Explicitly excluded: React, Angular, Vue, Node.js, any separate frontend build system,
MediatR/CQRS abstractions, generic repositories, service locator.

One permitted JS dependency: **SortableJS**, via minimal interop, for drag ordering only.
See D5 and §9 for why this is the riskiest line in the document.

`<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>` everywhere.

---

## 2. Architectural decisions and deviations

Numbered so they can be cited in code comments, PRs and future amendments.

### D1 — The queue gets its own table; `LibraryEntry.QueuePosition` is dropped

> **Superseded in part by D15, and finally by D23.** A franchise no longer occupies a queue
> slot, so the exclusive-or below is gone and `QueueItem.AnimeId` is required; D23 then removed
> franchises from the application altogether, so there is no longer any second kind of thing a
> slot could hold. The conclusion — a separate table — still stands, for the different reasons
> D15 gives. The rest of this entry is kept as the record of why the queue was modelled this
> way first.

*Brief §4 vs §5 contradict each other.* §4 puts `QueuePosition` on `LibraryEntry`; §5
requires that an anime **or an entire franchise** can occupy a queue slot. There is no
`LibraryEntry` row for a franchise, so a nullable int on that table structurally cannot
express "Slayers is at position 7".

**Decision:** a dedicated `QueueItem` table holding an exclusive-or reference:

```sql
CHECK ((AnimeId IS NULL) <> (FranchiseId IS NULL))
```

`LibraryEntry.QueuePosition` does not exist. "Is this queued, and where?" is a join.
Reordering rewrites one narrow table instead of touching library rows.

### D2 — No unique index on `(ProfileId, Position)`

SQLite enforces uniqueness **per statement**, not at commit. Any reorder that shifts a
block of positions collides transiently and aborts the transaction. The escapes (shift into
a negative range, then rewrite) are two passes of pure ceremony.

**Decision:** a plain non-unique index. Contiguity and uniqueness are invariants owned by
`QueueService`, applied inside a single transaction. This is a real trade — the database no
longer defends the invariant, so §8's reorder tests are load-bearing, not decorative.

### D3 — `IDbContextFactory`, never scoped `AddDbContext`

Under Interactive Server a scoped service lives as long as the SignalR circuit — the user's
whole session, potentially hours. A scoped `DbContext` there means an unbounded change
tracker, stale reads, and `InvalidOperationException` the first time two components render
concurrently.

**Decision:** `AddDbContextFactory<AniQueueDbContext>`; every service method creates and
disposes a short-lived context. This is the most common Blazor Server + EF defect and is
free to avoid at the start.

### D4 — Recommendation history needs per-run items

> **Superseded in part by D16.** A run item references one anime; the nullable
> `FranchiseId` below is gone. The rest of this entry — run plus per-candidate items, no
> persisted request payloads, `LibraryEntry` as the applied-run cache — stands, and it is
> that cache which forced D16.

*Brief §20 is internally inconsistent.* It says store run metadata and avoid duplicating
request data, then requires comparing the current recommendation set against a previous
one. Metadata alone cannot support that comparison.

**Decision:** `RecommendationRun` (metadata) + `RecommendationRunItem` (per-candidate rank,
predicted score, confidence, reason). Request payloads are **not** persisted — the
candidate set is reconstructable from run items. The `Recommendation*` columns on
`LibraryEntry` are a denormalised cache of the currently-applied run so backlog sorting
stays a single-table query.

### D5 — Button reordering ships before drag-and-drop

The brief requires both (§5) and mandates a non-drag path (§39). The buttons are trivial
and entirely server-side; drag is the risky half.

**Decision:** implement button reordering and its tests first. Drag layers onto an
already-correct reorder service. If the interop proves messy, the feature degrades to
something that already works rather than blocking the phase.

### D6 — Central package management and shared build props

Not in the brief, framework-native, no new dependency: `Directory.Build.props` for shared
compiler settings, `Directory.Packages.props` for versions pinned once across five
projects. Prevents version drift.

### D7 — `ProfileSettings` is a typed entity, not a key/value bag

Settings (§25 of the brief) are a fixed, known set. Typed columns are migratable, bindable
straight from the Settings page, and cannot rot into stringly-typed soup.

**Amended by D20.** Two kinds of setting turned out not to fit this entity: those an operator
must reach from outside a running container, which live in configuration, and those keyed per
external source rather than per profile, which get their own entity. The argument above is
unchanged for everything that remains here.

### D8 — `AniQueue.slnx`, not `AniQueue.sln`

*Reverses an earlier call in this document.* The brief names `AniQueue.sln`, and the first
attempt used the classic format on the reasoning that `.slnx`'s main benefit — no GUIDs, so
no merge conflicts — does not apply when all five projects are added at once and no more
are planned.

That weighed one factor and missed a second. `dotnet new sln` emits a **hardcoded, already
stale version stamp** (`# Visual Studio Version 17` — VS 2022) that has nothing to do with
the installed toolchain, and the format carries ~100 lines of GUID bookkeeping for what is
really a five-line list of projects. Since VS 2026 is the primary development environment
and supports `.slnx` natively, the modern format is the better fit.

**Decision:** `AniQueue.slnx`, generated via `dotnet sln migrate`. 101 lines → 12, no GUIDs,
and no version header that can go stale. Verified: `dotnet sln list`, `dotnet build`,
`dotnet test` and `dotnet build -c Release` all work unchanged. The `x86`/`x64` platform
entries the migration carried over were removed — this solution only ever builds Any CPU.

Minimum tooling this implies: VS 2026 (or VS 2022 17.14+) and the .NET 10 SDK. Both are
already the baseline in §0, so nothing is lost.

### D9 — Parsing lives in Core, and the parser does not build the preview

**Recorded in §5**, beside the import pipeline it describes, rather than repeated here. This
stub exists because the number is cited from code — `IAniListClient` and `AniListClient` both
point at it — and a decision that cannot be found from its own register is a decision nobody
will check before contradicting it.

### D10 — Franchise grouping waits for authoritative relation data

A MyAnimeList export carries no relationship data at all. Its 23 fields per entry are
catalogue basics and the user's own tracking; there is no sequel, prequel, parent or
franchise field. Franchises therefore cannot be derived from an import, only curated.

That is a problem at realistic scale. Measured against a genuine 752-title export, a
title-similarity heuristic proposed **138 candidate franchises covering 447 titles (59%)**.
Curating that by hand is data entry, not curation — but the same run also produced a
confident group named "Re" containing seven entries, which is `Re:Zero` split on its colon.

The brief permits detection "as an optional suggestion" (§4), and that option was considered
and **declined**: a suggestion engine that is confidently wrong leaves the user unpicking
mis-grouped franchises, which is worse than having none yet. Franchise grouping instead
waits for MAL/AniList relation data, which is authoritative rather than inferred.

**Consequence, accepted knowingly:** until that integration lands, users with large libraries
have manual franchise management only, and will realistically group a handful of franchises
rather than all of them. Phase 6 ships manual tools; the relation data D13 promoted into the MVP
is what makes franchises practical at scale.

Do not re-propose title-similarity detection without new evidence that it can be made
accurate — the 59% coverage figure is not the interesting number, the false positives are.

**Amended by D13.** Promoting AniList read access into the MVP supplies exactly the
authoritative relation data this decision was waiting for, so the wait is now until Phase 5
rather than until after the MVP. Phase 6 may propose groupings from real relations — still
confirmed by the user, never applied silently.

**Amended again by the Phase 5 split.** Relation data is now fetched *in Phase 6*, not handed
over by Phase 5. Because relations come from a separate query rather than riding along with the
list (see Phase 6), they have no coupling to list sync, and fetching them one phase before
anything consumed them split one feature across two phases for no benefit. The wait is
unchanged in substance: authoritative relations still precede any grouping proposal.

**Amended finally by D23 and D24, and the change is a reversal rather than a delay.** This
decision said grouping waits for authoritative data. The answer is now that grouping does not
happen at all: AniQueue never groups titles, curated or proposed, because grouping is
authorship and membership is not AniQueue's to author (D11 applied one level down). The
relation data this entry was waiting for is still fetched and is still the foundation of
Phase 6 — it is shown per title rather than resolved into groups. What survives intact is the
warning: **do not re-propose title-similarity detection.** It was wrong when the alternative
was curation and it is wrong now that the alternative is authoritative edges.

### D11 — The AI orders a closed set. It does not choose what is in it.

**What AniQueue is for:** a watch list whose *order* is maintained jointly by the user and
an AI, and which persists. The list's *membership* is maintained elsewhere — principally on
MyAnimeList or AniList — and arrives here by import or, later, sync.

That division is the point. AniQueue owns the order; the external service owns the
membership.

So the model is given the user's scored history and the titles already on their list, and
asked to rank *those*. It is never asked what to add. Entries it ranks lowest become
removal candidates the user may act on at their discretion; removal is therefore a
consequence of ranking rather than a separate instruction, and needs nothing in the schema.

This is a constraint on the model, chosen deliberately over a broader one:

- **It removes hallucination by construction, not by validation.** Every ranked item carries
  a candidate id AniQueue issued, so a fabricated title has nowhere to appear. Asking for
  additions would mean accepting titles the application has never seen, which would then
  need resolving against a real catalogue before they could be trusted — work that depends
  on the API integration, not the model.
- **It keeps the result schema exactly as the brief specifies.** An earlier suggestion to add
  an extensibility hook for future "suggested additions" is withdrawn: a field for a feature
  that is not being built is speculative infrastructure, and `schemaVersion` already provides
  the way to change shape later.
- **It needs no new persistence.** `RecommendationRunItem` referencing an existing anime or
  franchise holds, because every ranked item exists locally. Acting on a removal candidate
  uses what is already modelled — `Dropped` status or `IsHidden` — so nothing has to be
  written back to an external service.

Deferred until the core is done, and deliberately not designed yet: an LLM-written summary
of the user's taste for the dashboard. Pleasant, not load-bearing.

### D12 — AniQueue observes watched status. It never authors it.

D11 said membership is owned elsewhere. This follows it to its conclusion, and
**removes a phase**.

The brief's watching workflow (§22) — mark as watching, add an episode, complete and
score — assumed AniQueue was the primary tracker. It is not. A realistic setup already
has one: media server scrobbles to AniList, scores are entered there, and other services
mirror it. Every one of those actions in AniQueue would create a second source of truth
that drifts from the first within a day.

So watched status, episode progress and scores are **read-only here**. They arrive by
import or sync, they display, and nothing in the application writes them.

That extends to the action that looked most defensible. An explicit "start watching"
button is unnecessary, because starting a show is already observable: the entry moves from
Planning to Watching at the source. **The queue advances as a consequence of sync, not of a
click** — when a queued title stops being Planning, its slot is released and the next item
becomes next in line.

This has a useful property: the rule belongs to the import and sync path, not to a button,
so it works with file import today and needs no API to be correct.

The trade, recorded honestly: the brief's acceptance criteria 13 and 14 ask for progress
tracking and score entry, and this decision declines both. D11 and the brief disagree here;
D11 is the later and more considered position, and the application it describes is the one
being built.

**What remains of the old watching phase:** nothing that warrants a phase. Its one surviving
rule is queue advancement, which belongs with the queue in Phase 4.

### D13 — AniList read access moves into the MVP

Previously post-MVP. Two things move it.

**It is the only manual step in an otherwise automatic chain.** With D11 and D12, membership
and status both arrive from outside — so file export and upload is the single point where the
user does work a machine should. Automating the *ordering* while leaving the *input* manual
optimises the wrong half.

**D12 depends on it to be useful.** Queue advancement works on any import, but advancing on a
schedule rather than when the user remembers to export is what makes the queue trustworthy
without attention.

Scope is deliberately narrow: **read only.** List and status retrieval, and relation data.
No write-back, no OAuth-gated private lists, no scheduled re-ranking. Write-back stays
post-MVP, where it belongs — it is the direction that can damage data the user maintains
elsewhere.

Worth confirming at implementation time rather than assuming: AniList's GraphQL API serves
public list data without authentication, which if true removes OAuth from the MVP entirely.
Design for it, verify before relying on it.

**A consequence for D10:** franchise grouping was deferred for want of authoritative relation
data. Promoting AniList read access supplies it, so grouping can use real relations inside
the MVP rather than waiting. The phase order below puts AniList before relations for exactly
this reason.

*Amended by D23 and D24.* The relation data promoted here is still the point, but nothing
groups with it: relations are shown per title, and the franchise entity is gone.

**Scope, revised once the design was worked through.** Three things changed and are recorded
here rather than left as drift between this entry and the phase plan:

- **The phase is three phases** — 5a, 5b, 5c. Fourteen deliverables across five subsystems is
  not one reviewable change, and §9 already names scope as the main schedule risk.
- **The per-source watermark described below is not a wire optimisation.** `MediaListCollection`
  returns an entire list in one request, so a watermark-driven delta costs *more* requests than
  a full fetch — and worse, a delta is structurally blind to deletions, because a removed entry
  has no `updatedAt` to appear under. That makes it incompatible with D19, which is committed.
  The watermark's real jobs are refusing to re-poll inside the interval floor, skipping a commit
  that would change nothing, and rendering "last synced". That is bookkeeping, not a protocol.
- **Relation data moves to Phase 6**, where it is consumed. See the amendment on D10.

**Verified against the live API on 2026-08-17**, using a real 753-entry public list rather than
assumed. What was checked, and what it means:

| Assumption | Result |
|---|---|
| Public lists readable unauthenticated | **Yes.** HTTP 200 with full data, no `Authorization` header. **OAuth is out of the MVP entirely** |
| `MediaList.score` accepts `format:` | **Yes**, and the conversion is genuinely server-side — the same entry returns 7 / 70 / 4 / 3 across `POINT_10` / `POINT_100` / `POINT_5` / `POINT_3` |
| `MediaListCollection` returns a complete list | **Yes.** Unchunked, `hasNextChunk` is `false`; 753 entries and 753 distinct media ids arrive in **one request** of 424 KB at full fidelity |
| Rate limit | **30/min**, not the documented 90. `X-RateLimit-Limit`/`-Remaining` are returned and CORS-exposed |

That settles the watermark argument above with measurements rather than reasoning: one request
per poll, 424 KB, against a 30/min budget. There is no rate-limit problem for a delta to solve.

Field-level counts from the same response, which several decisions below now rest on:

| | |
|---|---|
| `idMal` null | **6 of 753** (0.8%) — the bridge gap in D17 is real but tiny |
| `title.english` null | **111 of 753** (14.7%) — D22's fallback chain is load-bearing, not defensive |
| `duration` null | **0** — every title carries an episode duration |
| `seasonYear` null | 13 (1.7%) |
| `episodes` null | 1 |
| `coverImage.large` null | 0 |
| `startedAt` entirely null | 208 (27.6%) |

**What the probe could not verify, because this library does not contain it.** Only `COMPLETED`,
`CURRENT` and `PLANNING` appear, so the `PAUSED`, `DROPPED` and `REPEATING` mappings are
reasoned rather than observed. No partial `FuzzyDate` occurred — every date was complete or
entirely null — so partial-date handling is untested against real data even though the schema
makes all three components independently nullable. And the account uses no custom lists, which
leaves one hazard open below.

**Custom lists are an open hazard.** `MediaListCollection.lists` carries `isCustomList`, and
AniList lets a user file one entry into a status list *and* custom lists. Whether that surfaces
the entry more than once in the collection is unverified — 753 entries to 753 distinct media ids
here proves only that it does not happen when no custom list exists. The parser must therefore
de-duplicate by media id rather than trust the collection to be flat.

**Two hardening notes from the response itself.** The endpoint sets a `laravel_session` cookie,
so the client should not persist cookies; and 424 KB for 753 entries means a library of a few
thousand is a few megabytes, which is what §6's response cap should be sized against with
headroom rather than tightly.

### D14 — No manual priority. The queue is the user's ordering.

`LibraryEntry.ManualPriority` is removed: the column, the filter, the sort, the facet and
the bulk action.

**A shared bucket is not an order.** Setting twenty titles to priority 5 says nothing about
which of them comes first, so a bulk priority control could not produce the thing it
appeared to produce. Ordering needs a rank, and two already exist — both real ranks:

| Ordering | Held by |
|---|---|
| The user's | `QueueItem.Position` |
| The AI's | `LibraryEntry.RecommendationScore` |

Those two are exactly what the Manual / AI / Hybrid views need. A third axis overlapping the
first without replacing it only blurred the distinction.

The brief lists manual priority as a hybrid ranking input (§18) — but the same sentence also
lists *"whether title is already in Up Next"*. Queue position was always an intended signal,
and it is the better one: being third in a hand-ordered queue is a far stronger statement of
intent than wearing a shared label.

Removed rather than left unused. Keeping a column against a possibility is the speculative
infrastructure argued against in D11, and if Phase 7 wants a user signal stronger than queue
membership, choosing one deliberately beats inheriting one nobody picked.

Two details worth keeping in mind when removing anything similar:

- **The retired sort's enum value is not reused.** Sort preferences are persisted in settings
  later, and silently changing what a stored number means is how a saved preference becomes a
  wrong one.
- **A tiebreak test quietly stopped testing anything.** It had sorted by priority across
  entries sharing a value; with priority gone it sorted unique titles and would have passed
  without exercising the tiebreak at all. It was re-pointed at a sort that genuinely collides.

### D15 — A franchise groups titles and queues them. It is not a queue item.

> **Superseded in part by D23.** Everything below about what a queue slot is remains in force
> and is the reason the queue survived franchises being deleted without a single change to its
> table. What is withdrawn is the other half — the franchise as a grouping, its curation, its
> collapsed card, `OptionalWithinFranchise`, and expansion taking a franchise id. The one-click
> expansion itself survives, rooted at a title and walking the relation graph forward (D24).

*Reverses the central claim of D1, and declines §262 of the brief — while still meeting the
acceptance criterion that rested on it.*

The brief (§262) allows "an anime **or** an entire franchise" to occupy a queue slot, and D1
built the schema around it: a dedicated `QueueItem` table with an exclusive-or reference,
because a franchise has no `LibraryEntry` row to hang a position on. That is now withdrawn.
`QueueItem` references exactly one anime.

**A franchise is not a thing you watch.** The queue answers one question — what do I press
play on tonight — and its unit has to be an answer to it. A slot holding twelve seasons is a
project, not an evening. Once one exists, position stops meaning anything: item 2 is ninety
minutes and item 3 is ninety hours, and "third in the queue" no longer describes when you
get to it.

**The mechanism that made it work was already deleted.** §262 only functioned because of
§302 — *starting the franchise selects the next unfinished anime in franchise order* — which
turned one slot into a sequence of real decisions. D12 removed the entire watching workflow,
that action with it. What was left was a queue element with no defined way to progress
through it, and implementing D12 exposed the gap immediately: releasing a franchise slot
needed a rule invented for the purpose ("release once nothing in it is still planned")
derivable from nothing. Having to invent that rule was the evidence.

**And it forbade the ordering the application exists to provide.** A franchise as one slot
makes it structurally impossible to put anything between two of its seasons, or to skip a
season, or to space a long series out across months. D11 says AniQueue owns the order of the
watch list. A model that prevents the user expressing an order contradicts the premise.

**Decision:** `QueueItem` is `Id, ProfileId, Position, AnimeId, AddedAt`. The exclusive-or
check constraint and the franchise index go. A franchise keeps everything else it had —
membership, `FranchiseOrder`, `OptionalWithinFranchise`, curation, the collapsed card — and
gains one job in the queue:

- **Queueing a franchise expands it.** Its members that are still Planning, not already
  queued, and (by default) not optional are appended individually in viewing order. One
  click, one decision, but what lands is a run of watchable titles.
- **In the queue a franchise is a badge**, so its seasons read as related while remaining
  independently orderable.
- **`OptionalWithinFranchise` gets a concrete job**: it decides what expansion queues. It
  was previously a flag waiting for completion maths to consume it.

**One membership rule, applied at both ends.** A queue slot holds a title the user
still plans to watch. `AddAnimeAsync` enforces it when a title goes in and
`AdvanceAsync` enforces it as statuses change — the same rule at two moments, rather
than a rule on the way out and nothing on the way in.

Expansion having that filter while the backlog's bulk add did not was a real defect,
found by noticing a Completed title sitting at the top of Up Next. Beyond the
inconsistency, allowing it created a slot with a hidden expiry: a watched title could
be queued and would then be deleted by the next import's advancement, without the
user having watched or removed it. Declining up front and **saying why** is the fix —
`QueueAddResult` separates "already there" from "already started or finished" from
"not in your library", because being told five of eight were added invites the guess
that the application lost the other three.

This does not carve out re-watching, and the way it doesn't matters: set the title
back to Planning at the source and it is queueable again. D12 has AniQueue observe
watch status rather than author it, so a re-watch is expressed where every other
status change is.

**AniList's `REPEATING` is not that gesture, and mapping it here would be a mistake.**
`REPEATING` means actively re-watching now, so it maps to `Watching` and its queue slot is
released — correct, because Up Next answers "what do I start next" and a show playing tonight
is not a decision waiting to be made. The re-watch above is a *planned* re-watch, expressed by
setting a completed title back to `PLANNING`, and that still works unchanged. Mapping
`REPEATING` to `Planning` to make re-watches queueable would put a show being watched into the
not-started bucket and break D12's premise that status is observed faithfully.

Three things fall out rather than being built:

- Advancement becomes per title. Watch season two and only that row leaves; season three
  rises to meet you. The invented franchise rule is gone.
- Re-adding a franchise after a sync brings a new season queues exactly the new season, from
  the same idempotency that already governs adding a title twice.
- Dissolving a franchise no longer empties part of the queue. Under D1 the slot was deleted
  with the franchise, silently taking the user's ordering with it.

**What is given up, plainly.** The queue can no longer show one line reading "Slayers"; a
five-season franchise is five rows, and that is the point — those are five evenings. The
brief's §264 complaint that sequels should not each be *"an independent high-level
choice"* is real, but it is a **backlog** problem, and it is answered where it occurs: by
grouping and collapsing in the backlog, which is the decision surface. The queue is the
ordering surface, and ordering wants the finer grain.

**Acceptance criterion 10 is still met.** It asks to *"add standalone anime or franchises to
Up Next"*, and that still works, from one control. What changed is what the queue holds
afterwards, which the criterion does not specify. Unlike D12's honest decline of criteria 13
and 14, nothing on the brief's completion list is lost here — only §262's account of how it
should be represented.

**Why the queue keeps its own table.** D1's reasoning is gone, so `LibraryEntry.QueuePosition`
would now be expressible. It is still refused. Reordering would write to wide library rows
that imports contend for on a single-writer database; the ordering column would be null on
most rows; and the queue has its own lifecycle. The conclusion survives its original argument.

**Left open deliberately:** `RecommendationRunItem` still carries a nullable `FranchiseId`
(D4), so Phase 7 could rank a franchise against individual titles — the same granularity
mismatch in the recommendation surface. It is not changed here because it is a question about
D11's model rather than the queue's, and it should be argued on its own terms before Phase 7.
**Settled by D16, in the affirmative.**

### D16 — A ranked candidate is a title. It cannot be a franchise.

*Settles the question D15 left open, and supersedes part of D4.* `RecommendationRunItem`
references exactly one anime; the nullable `FranchiseId` and its exclusive-or check
constraint are removed.

D15's granularity argument carries over unchanged — ranking a twelve-season group against a
single film compares a project to an evening. But the decisive reason is narrower and does
not depend on taste at all:

**A franchise ranking had nowhere to be applied.** Applying a run caches its result on
`LibraryEntry.Recommendation*`, which is what makes sorting the backlog by AI score a
single-table query (D4). A franchise has no `LibraryEntry` row. So a franchise placement
could be *stored* and never *applied* — the column permitted a row that the write path
structurally could not consume.

This was not a hypothetical. The one existing implementation of applying a run filtered its
items with `Where(i => i.AnimeId is not null)`, discarding franchise placements as its first
act. A field whose every consumer must skip it is not extensibility; it is an invitation to a
bug in Phase 7, where an imported ranking containing franchises would validate, persist,
report success, and change nothing.

Nothing produced such a row — not the seeder, not any test — so removal loses no behaviour.

**Consequence for Phase 7, stated plainly:** the AI ranks titles. If a user wants a
franchise's seasons ranked, they are ranked individually, which is also the only form in
which the result can be displayed or sorted. This costs nothing real: D11 already builds the
candidate set from the user's library, and every candidate in it is a `LibraryEntry`, so
franchises were never going to appear there in the first place.

**Not changed at the time:** `Anime.FranchiseId`, `FranchiseOrder`, `OptionalWithinFranchise`
and the `Franchise` entity all stood. Franchises remained a grouping, a backlog collapse and a
queueing action (D15) — they were simply not a unit of ranking, exactly as they were not a unit
of watching.

*That sentence is now false in every part: D23 deleted all four.* The reasoning above is
unaffected and, read in hindsight, was the first half of the same argument — a franchise turned
out to be not a unit of ranking, not a unit of watching, and finally not a unit of anything.

### D17 — External identity is a set, not a field

*Retires `Anime.Source` + `Anime.SourceAnimeId` as the identity mechanism. `Source` survives
as provenance.*

Phase 5's charter said reconciliation "reuses the import pipeline — matching on
`Source + SourceAnimeId`". That sentence is only true while both sides speak the same source.
A library imported from MyAnimeList holds `(MyAnimeList, <mal id>)` on every row; an AniList
sync arrives as `(AniList, <anilist id>)`, matches nothing, falls through to the title branch,
and returns a conflict or a duplicate for **every title in the library**. Against the genuine
752-title export measured in D10 that is either 750 hand decisions or 750 duplicate rows — and
unattended sync has nobody present to make decisions.

AniList publishes `Media.idMal`, so the bridge arrives in the same response that creates the
problem. What was missing was anywhere to keep it.

**Decision:** external identity moves to `AnimeExternalId (AnimeId, Source, ExternalId)`,
unique on `(Source, ExternalId)`. A title carries zero or more. Matching tries every
identifier an incoming entry supplies, in source-precedence order, before it considers a title.

Three consequences, one of them a tidy-up:

- **The filtered unique index disappears.** `IX_Anime_Source_SourceAnimeId` carries
  `WHERE SourceAnimeId IS NOT NULL` only because manual entries have no identifier and would
  otherwise collide on null. A manual entry now has no rows at all, so the constraint needs no
  filter.
- **`Source` narrows to provenance** — how this record came to exist — and stays on `Anime`
  because the seeder and conflict linking both reason about it. The backlog's source filter
  changes meaning with it, and improves: `Source == AniList` becomes "this title is on
  AniList" rather than "an AniList import created this row", which is what a user clicking the
  chip means.
- **A row can offer several links.** `SourceLink?` becomes a collection, so a bridged title
  links to both MyAnimeList and AniList.

Typed columns per platform — `MalId`, `AniListId`, later `KitsuId` — were considered and
declined. They are the arity-fixed denormalisation of the same relation, they need one filtered
index each, and reaching the general shape later means rewriting the matching path a second
time. Matching is the one path where a mistake silently duplicates or silently merges a user's
library, so it is worth getting right once; and the generality is free *now*, while the only
consumers are two projections and one filter. This is the argument Phase 11 makes about
squashing migrations — a cheap structural change has a window.

**Not every platform is a peer, and this table must not imply otherwise.** MyAnimeList,
AniList and Kitsu are anime databases with roughly 1:1 title identity that publish
cross-references to one another. Trakt, TMDB and TVDB are general-media databases where a
franchise is frequently one series with absolute episode numbering, the mapping is many-to-one,
and nobody publishes it — §10 already says exactly this about Overseerr. Storing such an
identifier here is harmless; assuming it is 1:1 and self-populating is not.

**The bridge works in both directions, provided a sync writes every identifier it is given.**
An AniList entry carries both `id` and `idMal`, so a sync that stores both leaves a
`(MyAnimeList, <mal id>)` row waiting, and a MyAnimeList export landing later matches it on the
ordinary path instead of conflicting. This extends no new trust: it is the same `idMal` claim,
from the same field, that the other direction already depends on. So a parsed entry carries a
**set** of identifiers rather than one, and a commit writes every one it does not already hold.

Two edges follow, and both must be caught while previewing rather than at the commit:

- **Two incoming entries claiming one identifier.** AniList holds split and duplicate entries
  that point at a single `idMal`. The second write would violate `UNIQUE(Source, ExternalId)`
  and fail the whole transaction, so the collision is detected during matching and reported as
  a problem against the entries that caused it.
- **One entry whose identifiers resolve to different local rows.** Trying identifiers in
  precedence order reads as first-match-wins, which silently discards contradicting evidence —
  and the contradiction usually means those two local rows are duplicates that ought to be
  merged. There is no merge surface, so this is a conflict for the user to resolve, never a
  silent pick.

**One narrow gap remains, and it is a data-quality gap rather than a hole in the model.** An
AniList entry with a null `idMal` asserts that no MyAnimeList counterpart exists; if the export
contains one anyway, the title conflicts on name and the user decides. Nothing in the design can
do better than the cross-reference it is given. Measured against a real 753-entry list, **6
entries had a null `idMal`** — under 1%, so the fallback is genuinely a corner rather than a
common path.

**The two identifiers are not interchangeable, and this is worth stating because assuming
otherwise is a tempting shortcut.** They frequently coincide — *Shingeki no Kyojin* is 16498 on
both — and then diverge without warning: its second season is AniList 20958 and MyAnimeList
25777. Code that treated one id as the other would appear to work across a sample and then
silently mis-map a sequel onto an unrelated title.

### D18 — A primary source owns tracking data. Others may only add.

Consolidating two separately-maintained lists is a use case AniQueue serves: a user with a
MyAnimeList history and an AniList list they now keep current has a real reason to want the
union. D17 makes it possible, and it makes the overlap contested — once a row carries both
identifiers, both sources can write its status, progress and score.

`UpsertLibraryEntryAsync` is unconditional last-writer-wins, which is right with one source and
wrong with two on a timer. The failure is concrete. A show on both lists is finished and scored
on MyAnimeList, the export is imported, and `AdvanceAsync` correctly releases its queue slot.
Thirty minutes later the scheduled AniList sync returns its stale `PLANNING`, unambiguously,
and applies it. The title reverts, becomes queueable again, and reappears in the backlog as a
decision already made. It flaps every interval, and the deliberate act loses every time,
because the scheduled writer is always the last writer.

**Decision:** each configured source carries a precedence rank. On a contested row a
lower-ranked source may create the row and fill catalogue metadata, but may not overwrite
status, episode progress or score written by a higher-ranked one.
`LibraryEntry.LastWrittenBySource` records who last wrote the tracking fields.

- **Precedence guards tracking data only.** Catalogue metadata — episode duration, release
  year, cover image — is filled by whoever has it, because AniList carries fields a
  MyAnimeList export simply does not. This is the line `UpsertLibraryEntryAsync` and
  `ApplyCatalogueFields` already draw between the user's tracking and facts about the title.
- **Ranking is explicit, never inferred.** "Whichever source syncs wins" is the obvious rule
  and it is wrong for the consolidator migrating *away* from AniList while treating
  MyAnimeList as authoritative.
- **With one source configured it never fires**, and behaviour is identical to today's. The
  single-tracker user D13 optimises for pays nothing.

Freshest-wins was rejected on availability: AniList supplies `MediaList.updatedAt`, and the
MyAnimeList export appears to carry no per-entry update time — the parser reads none. A rule
that cannot be evaluated for one side is not a rule. Monotonic merge — never regress status or
progress — was rejected because it breaks D15's re-watch story outright: a re-watch is
expressed by setting a title *back* to Planning, which is precisely the regression such a rule
would refuse.

### D19 — Absence is authoritative, but only where the source once spoke

D11 puts list membership outside AniQueue, which taken seriously means a title deleted from the
user's AniList list should leave here too. The import pipeline has no concept of absence — it
iterates a payload, so a title not in it is never considered — and so today the library only
ever grows.

Two populations must be told apart before any of this is safe, and D17 is what makes the
distinction exact:

- **A row with no identifier for the syncing source** has never been listed there. Out of
  scope, untouched, always. Every MyAnimeList-only and hand-added title is in this bucket.
- **A row carrying that source's identifier which the fetch did not return** was listed and is
  not now. Only this population is ever in scope.

That is what protects the consolidating user, and the protection is worth stating precisely
because it is *structural rather than configured*: their MyAnimeList-only titles are never at
risk whatever the setting says.

**Decision:** absence handling is configured per source — flag for review, remove, or ignore —
defaulting to **flag**. The default is safe for identical-list and consolidated-list users
alike, so correctness never depends on the user finding a setting.

- **Only the `LibraryEntry` and its queue slot are ever removed**, never the `Anime`. The
  catalogue row is shared with relation edges and `RecommendationRunItem` history.
- **Removing an entry must remove its queue slot in the same transaction.** `AdvanceAsync`
  deliberately treats a missing library entry as *unknown, not watched*, and keeps the slot.
  Deleting the entry alone destroys the only evidence that could ever release it, leaving a
  slot nothing can clear.
- **Automatic removal stays deferred, and D33 has withdrawn the milestone it was waiting for.**
  It was gated on a full backup and restore that is no longer being built; the recovery path is
  now the operator's own copy of the database file under `/data`, which is outside the
  application and outside a mistake it could make. A truncated response, a paging bug, a
  mistyped username or a profile turned private all look identical to "the user deleted
  everything", and an emptied library taking the hand-built queue with it is the one failure
  here that nothing in the product can undo.
- **When it does land it needs guards:** honour absence only when the fetch is structurally
  complete, never act on an empty or near-empty response, and cap the proportion removable in
  one unattended run before downgrading to flag.

### D20 — Operator configuration and user preference are different stores

*Amends D7 by adding a second home for settings, without reopening its argument.*
*Amended in turn by D36, which keeps the two stores and the disjointness rule but redraws the
line between them and lets the application write the file. The template, the load-failure
handling and the reasons for both stand unchanged.*

Phase 5 is the first phase with settings a self-hoster needs to reach from outside the
application, and the first with something the application must be able to be *told to stop
doing*. A single YAML file in `/data` was proposed for all of it, and declined.

**The stated goal was already met.** `Database:Path` is `/data/aniqueue.db`, so the database
already lives outside the image in the operator's volume, and criterion 25 already proves it
survives container recreation. Settings in the database are settings in `/data`. A second file
there is not more persistent; it is a second thing to hold state that has to agree with the
database, and a second place to look when it does not.

**Decision:** split by who owns the value, and keep the key sets **disjoint** so there is no
precedence puzzle between them.

| | Store | Holds |
|---|---|---|
| Operator / deployment | `IConfiguration` — `appsettings.json`, environment variables, optional `/data/userconfig.json` | Database path, port, sync kill-switch, poll-interval floor, the AniList account |
| User preference | Database, per profile, per D7 | Primary source, absence policy, unattended application, conflict policy, title language |

Because the sets are disjoint, a value changed in the UI can never be silently reverted by a
file, and the escape hatch that matters when the UI is unreachable — turning sync off — is a
configuration key by design.

`AddJsonFile(path, optional: true, reloadOnChange: true)` is one line and needs no package.
YAML needs YamlDotNet, which §12 requires approving, and a UI writing a hand-editable file
needs atomic replacement, concurrent-write protection, and comment-preserving round-tripping
that YamlDotNet does not provide. §9 also notes that a non-root container cannot write to a
root-owned bind-mounted `/data`, which would turn a first-run failure into a save button that
fails for Unraid users.

**Do not depend on live reload.** The file watcher behind `reloadOnChange` does not reliably
fire on Windows-host or network-share bind mounts, so a restart must apply the file too.

**The file is written on first boot, and everything in it is commented out.** A settings file
nobody knows exists is a poor escape hatch, and "create it yourself, in the right place, with the
right key names" is worse — so startup leaves a template naming every key it accepts. Three
properties make it safe rather than merely helpful, and all three are load-bearing:

- **It configures nothing.** The file is added *last*, so a key it sets beats the same key from
  an environment variable. A template shipping real values would therefore override the
  `Sync__AniList__UserName` an operator set in their compose file, on a machine where nobody had
  opened the file. Commented out, it can only take effect once somebody uncomments a line — an
  act that carries the intent to override.
- **One line per setting, written as a full `Sync:AniList:UserName` path.** The JSON provider
  reads a property name containing colons as the whole key, so this means the same as the nested
  spelling. Uncommenting a line out of a *nested* block leaves its closing braces behind, and a
  file that cannot be parsed is a file whose settings are all silently absent — a poor property
  for the one an operator edits when something is already wrong.
- **It never overwrites and never fails the boot.** An existing file is the operator's work,
  including one they emptied deliberately. §9's non-root container writing to a root-owned bind
  mount cannot create it at all, and refusing to start over an unwritable convenience file would
  turn a hint into an outage, so failure is a logged warning.

**A file it cannot parse must not stop the application.** By default the JSON provider throws
while the host is being built — before logging exists — so one missing comma in the file an
operator edits by hand replaces AniQueue with a stack trace on a console they may not be watching.
That is precisely backwards for an escape hatch: the file exists to be edited when something is
already wrong, and editing it is when it will be mistyped.

So the source is configured with `OnLoadException` set to ignore the load. Everything the file
would have configured is skipped, every other configuration source stands, the application starts,
and it says what happened — a warning at startup for the console, and a red banner on the
dashboard naming the file and quoting the parser's own message, which carries the line and
position. The provider's own load path decides what is acceptable rather than a validation pass of
ours: this file permits comments and trailing commas, and two implementations of that rule could
disagree. The same handler covers a file broken while the application is running, since a reload
failure arrives the same way.

`Database:Path` is deliberately not offered in it: the file is found by looking beside the
database, so a path set there could not be read until it was already in use.

**Per-source settings do not belong on `ProfileSettings`.** They are keyed
`(ProfileId, Source)`, so they get their own entity — which is also where D18's precedence rank
and D19's absence policy live.

### D21 — Unattended sync applies the unambiguous and holds the rest

*Amended by D40 and D42 in what operates this, never in what it decides. The fetch button and its
modal leave the Sources card; a background run that held something surfaces one "Review held
changes" button, which re-fetches inline and renders the same review. Review still persists
nothing, for the reason given below, and holding a fetch behind a button is what makes that
possible.*

The remote platform is where the user maintains their list, so the most recent sync is the
better record and AniQueue should accept it without asking. But nobody is present to answer a
question, and §6 forbids silently merging a match the application cannot confidently identify.

**"Unambiguous" is already computed.** `ImportAction.Create` and `ImportAction.Update` are
unambiguous; `ImportAction.Conflict` is by definition not. So the "safe subset" is Phase 2's
preview with conflicts withheld, and no new classification logic exists.

**Decision:** unattended sync commits creates and updates and holds conflicts. Both are
configurable per source — application defaulting to automatic, conflict handling defaulting to
review.

- **Review persists nothing.** A held preview is stale within the hour; the user's visit
  re-fetches and recomputes. Everything an earlier run applied returns as `Unchanged` and
  renders as nothing to do, so the pipeline gives this for free. Only the count is stored, for
  the badge.
- **`SyncRun` is the audit trail.** Unattended writes with nobody watching leave the log as the
  only record of what changed; a row per run carries counts and outcome, and doubles as the
  "last synced" the Sources page shows.
- **A sync that would change nothing writes nothing.** `HasApplicableChanges` gates the commit
  and the advancement, so an idle poll never contends for the single writer.

For AniList the conflict population is exactly **one shape**, which is what makes this
affordable. An AniList entry always carries an identifier, so it matches, bridges through
`idMal`, or is a clean create; the only remaining path is a local row with *no* identifiers
whose title matches exactly — a hand-added entry meeting the real thing. That shape decides
which resolutions may be automated:

- **`LinkToExisting` may be offered**, because it is the only resolution that converges:
  writing the identifier is what stops the entry conflicting on every subsequent sync.
  Choosing it is opting into silent title-based merging, which §6 otherwise forbids, so it is
  an explicit opt-in and is labelled as one. Two things make it defensible — the test is exact
  case-insensitive equality, not the similarity heuristic D10 rejected, and a genuinely
  ambiguous multi-match produces a conflict with no candidate id, which existing code already
  downgrades to skip.
- **`Skip` looks safe and does not converge.** The row stays unidentified and conflicts again
  on every sync, so the pending count never clears. What that choice really wants is skip
  *plus suppression* — a record that this pair was declined — which is the only part of this
  decision needing new persistence, and it is deferred.
- **`ImportAsNew` is never offered unattended.** It duplicates the row, both copies appear in
  the backlog, both are queueable, and no delete-duplicate surface exists. One toggle silently
  multiplying rows across a library is the same class of hazard as automatic removal without
  the guards D19 requires.

**A mandatory interactive first run was considered and dropped.** It was proposed because the
first sync is where a large unattended commit lands; with conflicts held by default the
remaining first-run change is D22's title rewrite, which is visible, reversible and not data
loss. It is recommended, not required.

### D22 — Title language is a preference, and a sync applies it

`Media.title` has four variants; a MyAnimeList export has one, roughly romaji.
`ApplyCatalogueFields` assigns `Title` unconditionally, so a first AniList sync rewrites the
displayed name of most of the library — *Shingeki no Kyojin* becoming *Attack on Titan* across
every row and queue slot — driven by a choice nobody made. Meanwhile
`AlternativeTitle` has existed since Phase 1 with nothing ever written to it.

**Decision:** the preferred language is a user setting — romaji, English or native. Each variant
is stored against its language and the preferred one is resolved into `Title`, so **changing the
preference rewrites the displayed titles immediately**, from what is already stored.

*As originally written this decision said a sync applied the change, on the reasoning below. That
was revisited in Phase 5b and is recorded after it — the reasoning was sound for the storage it
assumed, and the storage was the thing worth fixing.*

Triggering a sync is what makes this cheap. The next fetch rewrites `Title` through the same
`ApplyCatalogueFields` that set it originally, so there is no bulk update, no migration, and
none of the partial states a swap must guard — `Title` is required, and manual and
MyAnimeList-only rows have no alternative to swap with. It also behaves identically whether
the preference is changed before or after the first sync.

- **The resulting preview shows a title change on every AniList-known row.** With review on
  that is a long list. It is also honest: it is a library-wide change, and
  `CompareWithExisting` already renders it.
- **A missing variant falls back rather than writing null.** English is absent far more often
  than "occasionally" — **111 of 753 entries**, nearly one in seven, in the measured library. A
  preference of English without a fallback would push null into a `required` column for every
  one of them. One chain — romaji, English, native — is applied from whichever the preference
  names, over variants that each know which language they are.
- **`userPreferred` is not offered.** It depends on the AniList account's display setting,
  which would make captured test fixtures irreproducible.
- **MyAnimeList-only and manual rows are unaffected**, since there is no alternative to
  prefer. The Sources page should say so, or the setting reads as broken.

**Challenged in Phase 5b, and fixed there rather than deferred: this is display, not data.**
Re-fetching an entire list to change which of two stored strings is shown is a heavy mechanism for
a preference, and a library already up to date had nothing for the next sync to apply — so the
setting could not take effect at all until something else changed. The setting now behaves like
the theme does.

**What made it impossible was the schema, and that is what changed.** `AlternativeTitle` was a
bare string with nothing recording which language it held: the parser filled it with the next
variant that existed and differed, so it carried English for one row and native for the next.
Nothing could switch between them without guessing. Titles are now stored one column per language
— `TitleRomaji`, `TitleEnglish`, `TitleNative` — with `Title` kept as the resolved display value.

- **Typed columns rather than a title-per-row table**, for the reason D7 gives about settings: the
  set is fixed and known. Keeping the denormalised `Title` alongside them is what leaves the
  backlog's search, sort and paging as plain SQL over one column, where a join per query would
  have bought nothing.
- **Changing the preference rewrites `Title` in one statement**, from the variants already stored.
  No fetch, no network, no review list — and it reverses just as cheaply, because the variants
  are untouched by the switch.
- **The parser stopped having an opinion about language.** It used to take the preference, which
  needed an overload `IAnimeListParser` could not express and a second DI registration to reach.
  Both are gone: one parse now serves every preference.
- **The old column is dropped, not renamed.** The migration scaffolder proposed renaming it to
  `TitleRomaji`, which would assert that its contents are romaji — precisely the guess these
  columns exist to prevent. `Title` is untouched, so every library reads exactly as it did, and
  the next sync fills the variants in properly; a row whose variants are not yet known is in the
  same position a MyAnimeList-only title is in permanently.
- **Storing a variant counts as a change** in the preview, so an already-synced library actually
  records them on its next sync rather than resolving to the same displayed title and being
  skipped as unchanged.
- **Search reads every variant**, not only the displayed one: somebody reading English titles
  still knows the show as *Shingeki no Kyojin*.

*What is left for Phase 10* is only where the control lives — beside the theme, rather than on the
Sources page under a source that no longer has anything to do with it.

### D23 — Grouping is observed, never authored. The franchise entity is deleted.

*Generalises D12, withdraws the surviving half of D15, reverses D10, and declines acceptance
criterion 8 outright.*

D12 established that AniQueue observes watched status rather than authoring it. The same
sentence is now true of grouping, and after it **AniQueue authors exactly one thing: order.**
Everything else — what is on the list, whether it has been watched, what belongs with what —
is a fact about the user's library held somewhere that already maintains it.

`Franchise`, `Anime.FranchiseId`, `Anime.FranchiseOrder` and `Anime.OptionalWithinFranchise`
are deleted, along with `ProfileSettings.ShowOptionalFranchiseEntries`, `FranchiseFilter`,
`LibraryFacets.HasFranchises`, `AddFranchiseAsync`, `QueueableFranchise` and both
`FranchiseName` projections. Nothing replaces them under a different name.

**The argument is D11's, one level down.** D11 says the external service owns list membership
and AniQueue owns order. A franchise is membership of a set — the same kind of claim, made
about a different collection, and equally not ours to make. Once that is seen, curation stops
looking like a feature and starts looking like the work D10 already refused to hand the user:
at 752 titles, grouping by hand is data entry.

**It also removes the last thing in the model a user had to maintain.** Every other local
concept is either derived from a source or is the ordering itself. A franchise was neither: it
was a structure the user built, kept up to date as new seasons arrived, and lost if they ever
started again. That is friction inside an application whose entire purpose is answering one
question quickly.

**What replaces it is nothing, and that is the point.** Not automatic grouping either — D24
explains why derived groups were also rejected — but relations shown against the titles they
are facts about.

**Acceptance criterion 8 is declined**, in the same way and for the same kind of reason D12
declined 13 and 14: *"create/edit franchises"* asks for an authoring surface this application
should not have. Criterion 9 is answered differently rather than declined, and D24 states how.
The brief's §810 — *"franchises are central to the application"* — is the strongest claim this
roadmap has contradicted, and it is contradicted knowingly: what is central is the decision,
and franchises were one attempt at serving it.

**One loss, stated plainly.** A MyAnimeList-only library gets nothing from Phase 6 at all.
Relations are an AniList query keyed by AniList ids, and a library imported from a MAL export
carries none. Such a user previously had manual tools; now they have neither. The fix is the
id-mapping job in D25, and until it ships this is a real gap rather than an oversight.

**Also gone: user-created titles.** Nothing in the application ever created one — there is no
such page, and `AnimeSource.Manual` survives only as provenance on rows that arrived carrying
no identifier — so this costs no code. It is recorded because it is the same principle: a
title AniQueue invented would have no external identity, no relations and no membership
anywhere, which makes it exactly the kind of thing this application has decided not to own.

### D24 — Relations are a property of a title, not a grouping of titles

*Settles what replaces the franchise, and declines acceptance criterion 9's stated form.*

Having deleted curated grouping (D23), the obvious next move is to derive groups from AniList
relation edges — connected components over a chosen set of relation types, each collapsed to
one backlog row. **That was designed and then rejected.** There are no groups, derived or
otherwise. Every title keeps its own backlog row, and each row expands to show what it is
related to, every relative tagged with its actual relationship: prequel, sequel, side story,
spin-off, alternative, recap.

**AniList publishes no franchises.** It publishes edges and types. A derived group is therefore
not source parity — it is still AniQueue's inference, and the traversal rule behind it would be
a product decision with nothing behind it, since a wrong group cannot be unpicked once curation
is gone. That is a reason for caution rather than for abandonment. What decides it is next.

**Artwork and collapse are substitutes, and the roadmap had them as complements.** §10 argues
that a backlog of several hundred rows is a wall of text and that recognising a show by its art
is faster than reading its title — then cites that as *supporting* the case for grouping. It
does the opposite. Both solve the same problem, so making the wall scannable with art removes
most of the reason to shorten it by hiding rows. The art is measured and already in hand:
`coverImage.extraLarge` is null for **0 of 753** titles and Phase 5b already fetches it.

**And hiding rows costs discoverability, which is half of what a backlog is for.** A collapsed
group shows one title where the user owns five. The other four stop being visible objects — no
art, no year, no runtime, no AI score — and a later season the user might want to start is
reachable only by expanding something. A backlog is not only a queue of decisions waiting to be
made; it is where interest is provoked. Collapsing optimises the first at the expense of the
second.

**Consequences, all of which make the build smaller:**

- **No components, no lead-row selection, no group key, no group name.** The naming problem
  disappears with the group: no synthesised label to get wrong, and no stored name to fall out
  of step with D22 when the title language changes.
- **Two edge sets, for two jobs.** *Display* — `PREQUEL, SEQUEL, SIDE_STORY, PARENT,
  ALTERNATIVE, SPIN_OFF, SUMMARY, COMPILATION, CONTAINS` — is deliberately wide, because
  nothing is merged and everything is labelled. *Queue expansion* is `SEQUEL` walked forward
  only, skipping `SUMMARY` and `COMPILATION`. `CHARACTER`, `ADAPTATION`, `SOURCE` and `OTHER`
  are in neither: the first links unrelated shows through a shared character, the next two
  point at manga, and the last has no meaning to label it with.
- **Expansion survives D15, rooted at a title.** "Queue this and what follows" walks `SEQUEL`
  forward from the title in front of the user and appends what is still Planning, in release
  order. It is strictly better than franchise expansion was, because it never proposes the
  prequels they have already watched.
- **`OptionalWithinFranchise` is not replaced by another flag.** What it meant is derived: a
  recap or a compilation is skippable because of what the edge says it is, not because somebody
  ticked a box.
- **A standalone filter survives, redefined.** Brief §345 asked for franchise/standalone; the
  useful half is *standalone*, computed as "no `PREQUEL` or `SEQUEL` edge at all" — a real
  decision (*something self-contained tonight*) sitting naturally beside the runtime filter.
  Counted over all edges rather than only owned ones: a series whose later seasons the user
  does not own is still a commitment.
- **An expansion lists owned titles only.** Relations reach thousands of titles the user has
  never expressed interest in, and putting those into the decision surface is what D11 forbids
  — doubly so while there is no write-back, since the only available action would be "go add
  this somewhere else yourself".

**Ordering inside an expansion is release order, and is not claimed to be anything else.**
AniList publishes no viewing sequence. A topological sort along prequel edges produces *story*
order, which is frequently the wrong watch order — AniList marks `Fate/Zero` as a prequel of
`Fate/stay night`, so story order puts it first. Release order is a fact the source supplies;
story order is a curatorial opinion, and D23 has just finished establishing that those are not
ours to author. It needs a date finer than `ReleaseYear`, since split-cour seasons share one.

**Acceptance criterion 9 — *"collapse sequels into them"* — is declined in its stated form.**
What it wanted is met differently: sequels are visibly related, labelled, and one click from
the queue. What is not met is the collapsing, and the roadmap's third problem statement is
amended to match rather than quietly left standing.

**Left open deliberately:** an opt-in *"hide sequels I can't start yet"* filter — the lead-row
selection this decision rejected as a page shape, offered as a toggle instead. Not built.
Recorded so that if a large backlog does prove noisy, the answer is a filter the user chooses
rather than a structure imposed on everyone.

### D25 — Enrichment is a chain of gated jobs, and it is unauthenticated

> **Amended by D34.** The tiering below survives; its schedule does not. Richer TVDB and TMDB
> artwork is no longer post-MVP — it is Phase 9, with the id-mapping job — because the cost this
> entry prices, the filesystem cache under `/data`, is paid by the first cached image whatever
> is cached. Everything else here stands: the gating, the ban on authentication, silent
> degradation, add-only, and both schema warnings.
>
> **Amended again by D46 and D47.** D46 reads the licence this entry left unread and changes
> which dataset the id-mapping job takes. D47 builds the artwork half and settles what this
> entry only predicted: the second schema warning below arrives as `AnimeImage`, and the fetch
> it describes turns out to be the first outbound request AniQueue makes to an address it did
> not choose.

*Promotes §10's artwork tiers from stretch-goal prose into a planned shape, and fixes what the
relation backfill in Phase 6 is the first instance of.*

AniQueue ends up fetching several kinds of metadata a list sync never hands over: relations
(Phase 6), cross-service identifiers, and artwork. `IBackgroundJob` was written anticipating
exactly this. Four rules, decided once:

- **Each job gates on its own precondition; sequencing is emergent.** The relation job takes
  titles whose relations are not yet known, the id-mapping job takes titles with no mapping,
  the artwork job takes titles with a mapping and no cached image. Nothing orchestrates them,
  order falls out of data readiness, and each is idle when its input is empty — so a job can be
  added, disabled or rerun without touching the others.
- **No authentication, ever, for enrichment.** Catalogue metadata is public, which is what
  keeps OAuth out of the MVP (D13). Authentication would buy private lists and write-back, and
  write-back is the one direction that can damage a list the user maintains elsewhere. If OAuth
  ever arrives it arrives for its own reasons, argued on their own terms, rather than smuggled
  in by a metadata pass.
- **Enrichment degrades silently.** Every use of it is an enhancement, so a failed fetch logs
  and retries rather than raising a banner. Deliberately unlike sync, where D21 makes a stalled
  run visible: a stalled sync means the library is wrong, while a stalled backfill means one
  row is missing a detail.
- **Enrichment may only add.** D18 already governs this — a primary source owns tracking data
  and others may only fill gaps — so an enrichment job never touches status, progress or score.

**Two schema warnings carried forward from §10 so they are not rediscovered late.** TVDB and
TMDB identifiers **do not fit `AnimeExternalId`**: they are many-to-one and meaningless without
the season the mapping dataset supplies alongside them, so storing them as peers of an AniList
id would claim an identity they do not have. And more than one image per title **kills
`Anime.CoverImageUrl`** — poster, banner, logo and backdrop are a set, which is the arity-1
mistake D17 has just finished undoing for identity. Both want their own tables.

**Artwork is promoted out of stretch goals**, because it is a primary decision input rather
than decoration, and because §10's own argument for it is stronger than the section it was
filed under. It splits by cost: `coverImage.color` is six bytes at 92% coverage and rides along
with Phase 6's query change; cached cover rendering becomes its own slice inside the MVP,
before Phase 10, so the accessibility and responsive pass happens on the layout that actually
ships; richer TMDB and TVDB art stays post-MVP with the id-mapping job. The middle tier is the
real work, and its cost is the filesystem cache under `/data` that §9's non-root bind-mount
problem already blocks for the database.

*The licence was read, and the answer was not the one this entry expected.* See D46: `Fribb/anime-lists`
has no licence at all, which settles the vendoring question by removing the option, and the
dataset that replaces it is chosen partly for having one.

### D26 — Actions live on the row they act on, and the backlog has no selection

*Reverses the "bulk selection, bulk queue-add and bulk hide" half of Phase 3, on use rather
than on argument.*

The backlog shipped with a checkbox per row, a select-all in the header, and a bar that
appeared above the table once anything was ticked, carrying **Add to Up Next**, **Hide** and
**Clear selection**. Every row now carries its own **+** and its own hide toggle instead, and
the bar, the checkboxes and the selection state are gone.

**Selection makes a one-title action cost four steps.** Queueing one show meant tick, look up,
find the bar, press, and then clear the selection before touching anything else. That is the
*common* case — a backlog is read one interesting row at a time, and the decision it exists to
support is singular by construction. Bulk was the affordance, and it was priced as though it
were the default.

**The bar appears where the row is not.** It renders above the table, so acting on row forty
means reading row forty, moving to the top of the page, and pressing a button that does not say
which row it is about. That is the specific complaint, and it is not a layout detail: a control
that materialises somewhere else has already broken the connection to the thing it acts on.

**A disabled button beats a message.** With one title per press, the reason an add can be
declined — no longer Planning — is knowable before the click, so the button is disabled and its
tooltip says which. The whole `QueueAddText` apparatus that enumerated skipped counts goes with
the selection that made it necessary, and the page loses its status banner entirely.
`QueueAddResult` keeps its per-reason counts: **6d still adds a run of sequels in one press**,
and that is the case a summary was written for.

**The button is a toggle, not a one-way add**, showing `+` or `−` for the two states a title
can be in. A backlog row is where the mistake is noticed, and undoing it used to mean leaving
for Up Next, finding the row again there, and pressing a different control with a different
glyph — for something the row was already reporting with its own badge. So `IQueueService`
gains `RemoveAnimeAsync`: the same removal addressed by title, because a listing of titles
never sees a slot id and should not have to carry one to undo itself.

*Leaving the queue is allowed whatever the status*, unlike joining it. A title that stopped
being Planning while it sat in a slot is exactly the row somebody would want to clear by hand
rather than wait for the next sync to advance past.

*Plus and minus rather than Up Next's cross*, and the difference is not cosmetic. That page's
subject is an ordered list, and its cross removes a position from one. Here the pair reads as a
single state — in the queue or not — which is all a backlog row has to say about it. Neither
touches the library, and neither is `AdvanceAsync`: advancement releases a slot because a title
stopped being planned, which is an observation (D12), while this is the user changing their
mind about the order, which is the one thing AniQueue authors (D11).

*Re-adding goes to the back.* Position is authored rather than remembered, so restoring a
title's old place would be AniQueue holding an opinion about the order.

**Nothing is re-read after a press.** The row already shows everything the action changed, so
re-running the query would move rows under the cursor, close every open expansion and lose the
reader's place — to report a change they had just made. The page keeps a small overlay of what
it has done since it loaded, and discards it whenever the list is genuinely re-read.

**Hiding stays in place rather than vanishing.** A row that disappeared the instant it was
hidden would read as a delete, so it stays, dimmed, wearing its badge, with the button that did
it offering to undo it. It drops out on the next read, which is when the filter is next
honestly applied.

**Adding leads the row and hiding ends it, with the table between them.** They shipped as
neighbours in one actions cell, which put the action done constantly a few pixels from the one
that takes a title out of the list — two icon buttons of the same size, with nothing but aim
between them. Frequency decides the position: the plus goes where the eye already is, at the
start of the row beside the title being judged, and hiding goes to the far edge where a rare
action belongs. **The relatives inside an expansion lead with theirs too**, so one run of queue
buttons goes down the page whether a title is on a row or inside a panel — the control is the
same control, and it lives in the same place.

Both are the same component for that reason. Two copies of *when may this be pressed, and what
does it say it will do* is how a row's badge and its button start disagreeing.

**Hidden becomes a view in the status picker, and the "Show hidden" chip is deleted.** The chip
put the only route back to a set-aside title in the same row as *Under 2 hours* — findable if
you already knew it was there, and not otherwise, which is a poor place for the undo of an
action that is now one press on every row. It is a view rather than a filter, so it belongs
with the statuses: *what am I looking at* is one question with one control.

Three consequences, none of them incidental:

- **Two states, not three.** `IncludeHidden` mixed hidden entries back in among the visible
  ones, which answers no question anybody asks. `HiddenOnly` replaces it: either the backlog is
  being read, where hidden means hidden, or what was set aside is being looked for, where
  everything else is noise.
- **The hidden view ignores the status filter**, because hiding is orthogonal to status — an
  entry is hidden *and* Planning. Carrying a status into it would answer "what have I set
  aside" with only part of it, and that list exists to find something to put back.
- **Status counts now exclude hidden entries.** They did not, so "Planning (8)" produced seven
  rows. Harmless while hiding was a rare bulk action; not harmless now, and a picker whose
  counts disagree with its own results is worse than one with no counts at all.

Two smaller things follow. The **hidden option survives unhiding the last entry** while it is
the one being looked at, or the selected option would vanish from under the user mid-task. And
an **empty hidden list says "Nothing is hidden"** rather than offering to clear filters: it is
the expected end state of the job that list exists for, not a dead end.

**What is actually lost is hiding many titles at once**, and that is the honest cost. Queueing
many was never a real loss — the queue is an *order*, and a batch appended in title order is
not one, which is why 6d walks sequels in release order rather than offering a bigger
multi-select. Mass hiding is the case with no per-row equivalent, and it is bet against: hiding
is how a user says "not this one", which is a judgement made one title at a time. **If a real
need for it appears** the answer is a filtered bulk action — *hide everything matching these
filters* — rather than the return of the checkbox, because that is the form the need would
actually take.

**The busy dialog goes too.** It was there because SQLite's provider is synchronous and a bulk
write awaited inline freezes the circuit. One row's write is not a bulk write, and a modal
covering the page to announce it would be more disruptive than the wait it describes.

### D27 — A first run starts empty, and the development seeder is deleted

*Withdraws the sample data Phase 1 shipped, on evidence rather than taste.*

The seeder wrote fourteen titles, a queue and an applied recommendation run into any empty
development database. Phase 6c gave five of them AniList identifiers so the relation expansion
could be seen on `F5`, and invented those identifiers so a link out could not land confidently
on somebody else's show.

**That is what broke it, and the failure is structural rather than a detail of which numbers
were chosen.** An identifier the source does not issue is indistinguishable, to D19's absence
policy, from one it has stopped listing — so the first real sync against a real account
correctly reported five titles as *no longer on AniList*. A warning about data the application
had invented for itself, on the first run of a fresh install, phrased in the same words a
genuine deletion would use.

**Real identifiers would not have fixed it.** They would match only if the developer's own list
happened to contain those exact titles, and would report the same warning otherwise — the same
noise from a different cause, with a broken link out as the consolation.

**The sample data is not worth a workaround.** It exists to make an empty install explorable,
and every route into real data — a MyAnimeList export, an AniList sync — is one action away on
a page the empty state already points at. Set against that: sample rows that must be recognised
and cleared before the application can be trusted about the library, and a permanent obligation
on every future feature to reason about titles that do not exist. The empty state is now the
first thing a developer sees, which is also the first thing a *user* sees, and that surface has
never had a reason to be exercised before.

**What is lost is honestly a cost.** The inner loop no longer shows a populated backlog on `F5`
— seeing rows means importing or syncing first, which is one step where it used to be none.

**And that cost was paid immediately, which is why the seeder came back — on request.** In the
same change, the crash that emptying Up Next caused could not be verified at all: reproducing it
needs rows in a queue, and with nothing seeded there was no offline way to get any. A fix
reasoned from a stack trace and shipped unexercised is exactly what sample data is for. So
`SampleDataSeeder` exists again behind **two locks** — the `SeedSampleData` switch, and a
development-environment check that stops production resolving the type — and the automatic
behaviour is what stays deleted. The default is still an empty database, which is still the
first thing both a developer and a user see.

Three things make the returning version safer than the one that was removed:

- **It has to be asked for**, by switch or by the *sample data* launch profile, which also
  points it at its own database file. Nobody meets sample titles on a run they did not ask for.
- **It leaves AniList sync switched off in the database it seeds**, so nothing unattended goes
  looking. The absence report that started all this only happens if somebody presses *Sync now*
  afterwards — which is then a decision to mix sample data with a real account, and the report
  is correct.
- **It seeds a hidden entry**, because the hidden view and its status-picker option only exist
  when something is hidden, and a surface reachable only after hiding a row by hand is one
  nobody checks.

**Sample data and a real account remain alternatives, not complements.** That is stated rather
than engineered around: there is no way to hold invented identifiers in a library that also
syncs a real list without D19 noticing, and D19 noticing is the behaviour that protects real
libraries. Seed a database or sync one.

### D28 — Enrichment wakes on a library change, without anything orchestrating it

*Extended by D41, which gives every job the broadcast only the two sync entry points had, and adds
the rule for how much work a wake-up is worth. The test stated here is the one D41 keeps.*

*Amends D25's "sequencing is emergent" with the one thing emergence was missing: a reason to
look.*

Every enrichment job gates on its own precondition and runs on a timer. That is sound, and it
left the relation backfill up to fifteen minutes behind a sync — so syncing several hundred new
titles produced a backlog with no relations on it, and a Sources page whose **Refresh related
titles** button looked like the only way to fill them in. An automatic job being operated by
hand is a design that has failed regardless of what its timer says.

**The signal is that the library changed, not that a job should run**, and that distinction is
what keeps D25 intact. `BackgroundJobRunner` waits on its timer *or* on
`ILibraryChangeNotifier`, whichever comes first. Nothing names another job, no job knows what
any other does, and a runner woken with nothing to do finds nothing and goes back to waiting —
which is exactly what makes a broadcast safe. Remove the signal and everything still works,
fifteen minutes later; that is the test of whether it is orchestration.

**The manual sync had to start publishing**, which it never did. Only the unattended job
announced its commits, so a foreground sync left every other open page stale and every
enrichment job asleep. Both entry points now say the same thing.

### D29 — Catalogue metadata fills gaps; precedence settles disagreements

*Splits in two what D18 stated as one thing, on evidence from a real consolidating library.*

D18 says precedence guards tracking data — status, progress, score — and that catalogue
metadata is writable by any source whatever its rank. The reason it gives is that *AniList
carries fields a MyAnimeList export simply does not*, and refusing those would lose data for no
reason. That is an argument about **gaps**. The implementation was **last-write-wins**, which is
a different rule, and the difference is invisible until two sources both have a value and
disagree.

A user synced AniList as primary and then imported a MyAnimeList export. Three hundred and
thirty-one titles came back as updates: `Type: Ova → Special`, and `Title: 'SPY×FAMILY' → 'Spy x
Family'`. Nothing was being filled in. Two catalogues disagree about categorisation and about
punctuation, and the tie was going to whichever import ran last — so the media type of a title,
and the Type filter over the whole library, depended on import order.

**The same argument the roadmap already accepted elsewhere.** Phase 6b's backfill deliberately
does not write `ReleaseYear` from `startDate`, because "writing one from the other would fight
the next sync for the column, and the decade filter would flip between runs." Identical shape;
it had simply not been applied here.

**The rule, and it is asked per title rather than globally.** A source writes catalogue fields
freely when it outranks every other source that also identifies *that row*. Otherwise it may
only fill in what is missing. Rank means nothing where one source is the only one describing a
title, so a single-tracker library and a re-import of a corrected export behave exactly as they
always did — which is what stops this becoming a first-writer-wins lock.

**A source nobody has configured ranks below one somebody has.** The alternative is a tie, and a
tie means last-write-wins, which is the behaviour being removed. It also matches what the
setting means to read: someone who went to the Sources page and named a primary said something,
and a source they have never opened has not.

**Absence never erases.** A source that does not carry a field leaves it alone whatever its
rank. A MyAnimeList export knows no episode duration, and reading that silence as "there isn't
one" would discard the answer AniList already gave.

**The title half was a plain bug, not a policy question.** The display title was resolved from
the *incoming entry's* variants and assigned unconditionally — alone among the fields, every
other one being guarded. A MyAnimeList export has no variants, so it fell through to that
export's single unlabelled name, while the next three lines merged the variants and kept
AniList's. Rows were left holding a `Title` that disagreed with their own `TitleRomaji`, and the
change did not even persist: `RewriteDisplayTitlesAsync` resolves from the *row's* variants, so
changing the title language and back undid it. That is exactly the arity-1 ambiguity D22 removed,
reappearing one level up. Resolving from the merged row makes the import and the language
setting agree by construction rather than by a comment claiming they are tested together.

**Not yet settled, and it limits this.** `PrecedenceRank` is per-source and independently
settable, so two sources can both be rank 0 — and two primaries tie, which is last-write-wins
again. The Sources page also only offers the setting for AniList, so MyAnimeList's rank is
whatever the default says. *Settled by D30.*

### D30 — Sources is where every source lives, and primary is a single seat

*Finishes D29, which could describe precedence but not let anyone configure it.*

MyAnimeList was a source everywhere except on the page named after sources. Its export was a
separate **Import** item in the navigation, it had no settings of its own, and its rank was
therefore whatever the default happened to be — so D29's rule, which turns entirely on which
source outranks which, could not actually be exercised by a user. Three consequences follow, and
they are one change.

**Every source the application knows appears on the page**, whether or not anything can be
fetched from it. The only real difference is that one has a list to go and read and the other
does not, so that is one flag — `CanFetch` — rather than a second page and a second concept.
Everything else about them is already identical: both name titles, both rank, both produce a
preview, and both commit through the same pipeline.

- The AniList card keeps its account line and **Sync now**; the MyAnimeList card offers a file
  picker. That asymmetry is the whole of it.
- Scheduling, conflicts and absence are gated on `CanFetch`, because they describe what a *run*
  does and nothing runs on a file source. Gated rather than disabled: a disabled control still
  claims it could apply.
- The unattended job skips anything it cannot fetch. The status list widened, and without that
  guard a scheduled run would have asked to sync a file.

**The two previews merge, because they were never different.** §5 already said the difference
between an upload and a sync is the trigger rather than the logic; the page now says it too. A
fetch is kept whole where a file is not, but only because applying a fetch records a `SyncRun`
— a scheduled thing that fails unattended and must be able to say when it last worked — while a
file has no history worth keeping, since the user was standing there.

**Primary is a radio, not a per-source dropdown, and that is the substance rather than the
styling.** Two dropdowns could both say *Primary*, and two primaries tie — which D29 settles by
letting the last import win, the exact behaviour the setting exists to end. A control able to
express an unreachable state eventually will. So promotion is the only operation:
`SetPrimarySourceAsync` seats one source and demotes the rest in one transaction, and there is
deliberately **no demote** — taking the seat from its only holder would leave it empty.

Two smaller things follow from being able to see both cards at once. **Nothing is primary until
somebody chooses**: the entity defaulted the rank to zero, so every unconfigured source claimed
the seat and two claimed it simultaneously — and it disagreed with the import, which already
ranks an unconfigured source below a configured one (D29). And **the title language moved out of
AniList's settings** onto the page, because it is a profile preference rather than a fact about
a source; with a second card it would have been drawn twice, as two controls over one setting.

**The Import page is retired**, and both empty states now offer one destination rather than two.
Where the library comes from is one question with one answer.

### D31 — Scoring is a schema. Hosting a model is optional infrastructure.

*Splits the old Phase 9 in two and renumbers what followed. The old Phase 7 and Phase 8 are
withdrawn by D32 and D33.*

The AI half of this application was one phase that did two unrelated things: define what a
model is told and what it may answer, and arrange for something to carry it. Those have
different failure modes and different reasons to exist, so they are now Phase 7 and Phase 8.

**The schema is the product, and it comes first.** Phase 7 defines the export and — the part
that matters — the exact response AniQueue will accept, validated strictly and applied only in
full. That contract is what makes any provider substitutable: the manual copy-paste path and a
hosted endpoint are the same contract carried by different couriers, so the second is additive
rather than a second pipeline. It is the D9 argument applied one layer up.

**A hosted model is never required.** Phase 8's endpoint is a self-hosted one — LM Studio,
Ollama, anything speaking a chat-completions API — so §7's README promise holds literally: v1
AI recommendation works without giving AniQueue an API key. It also works with nothing
configured at all, which is the normal state of a fresh install and the reason Phase 7's manual
path is permanent rather than a stepping stone.

**The endpoint is operator configuration, and this is a security property** (D20). A server that
will POST to an address a page supplies is an SSRF; keeping the address in `userconfig.json`
and out of every browser-writable surface is how that is prevented rather than mitigated.

*Amended by D36 and D38.* The address stayed in `userconfig.json` and the page learned to write
that file, so the protection this paragraph describes is replaced rather than dropped: D38 names
what an entered address may be, and bounds what a failing endpoint may say back. The reasoning
above is why those guards exist and is kept for that reason.

**The reply is data and stays data.** §6 already forbids executing or evaluating AI content.
What that means concretely here: the response is validated against the schema, what survives is
a rank, a predicted score, a confidence and a reason, and those write to four columns on
`LibraryEntry` and a `RecommendationRun`. Malformed output fails and is reported. It is never
repaired by inference, because a guess about what a model meant produces a score the user
cannot audit — and an unauditable score is precisely what "no black box" was meant to exclude.

### D32 — The decision screen is declined; the backlog already is one

The brief's §8 asked for a "what should I watch?" screen — Anything / Something short / A movie
/ One evening / Old-school / From my top 20 / Surprise me — and the roadmap carried it as a
phase for a long time. It is withdrawn, along with the dashboard's Suggested Next panel.

**Both signals it was specified against have since been deleted.** The brief says selection
"should respect manual priority and, where available, recommendation scores". Manual priority
was removed by D14, because a 0–5 bucket shared by many entries expresses no order. That leaves
the recommendation score, which does not exist until a scoring run has been applied — so on
every fresh install, and on every install of anyone who never runs one, the screen has nothing
to rank by. *From my top 20* is the sharpest case: neither of the two things that could have
defined "top" survives.

**What remains of §8 is filters, and the backlog has all of them.** *Something short* and *One
evening* are `MaxRuntimeMinutes`, already offered as the Under 2 hours and Under 6 hours chips.
*A movie* is `MediaType`. *Old-school* is `Decade`. *Anything* is no filter. Rebuilding those as
a second surface would mean two places that answer the same question and drift apart, and it
was the strongest argument the phase had.

**`IRankingCalculator` goes with it.** Its consumers were Suggested Next and the Manual/AI/
Hybrid views, and a hybrid formula blending queue position with an AI score is only meaningful
once both exist and someone has asked for the blend. `RecommendationMode` and
`ProfileSettings.DefaultRecommendationMode` remain in the schema unused; §10 records hybrid
ranking as a stretch goal rather than a gap. Reinstating it is a formula and a sort, not an
architecture.

*What is lost is real and is worth naming:* the decision moment now ends where it already ended
in practice — at the top of Up Next, which the user reads before leaving the application. The
open question the old phase recorded about that is unchanged, and is not answered by keeping a
panel that cannot rank.

### D33 — The database file is the backup

The brief's criteria 23–24 ask for a full-library JSON export and a restore from it. Declined.

**It is a second persistence format for one that already exists as a single file.** A
self-hosted application's backup is `/data`, and Phase 11 already has to prove the database
survives a container being recreated — criterion 25 tests the same property that 23–24 were
protecting. A JSON round trip would additionally have to carry queue order, hidden flags,
settings, external identifier sets and run history, and stay backwards compatible across every
future migration. That is a schema maintained twice, and the copy that is not the schema is the
one that silently falls behind.

**It also could not have gone through the seam §5 assigned it.** `AniQueueJsonParser` was listed
as an `IAnimeListParser`, but that interface produces `ParsedLibraryEntry` — status, score,
progress — and a restore has to carry things a parsed *list* entry has no room for. The format
was specified before 5a, 6b and the queue table existed, and never revisited.

Phase 7 still exports JSON. It exports what a ranking needs, which is not a restore and is not
described as one.

### D34 — Enrichment is promoted into the MVP, and publishing waits on the security pass

Two amendments, related only in that both change when something happens rather than what it is.

**D25 filed richer TVDB and TMDB artwork as post-MVP** and kept only cached AniList covers
inside it. That is reversed: artwork is a decision input rather than decoration — a backlog of
several hundred rows is read by its covers — and the expensive part, the filesystem cache under
`/data` and its non-root bind-mount permissions, is paid the moment any image is cached at all.
Everything else D25 decided stands unchanged, including the two schema warnings, the ban on
authentication for enrichment, and silent degradation. The id-mapping and artwork jobs are
Phase 9.

*Its recorded blocker was on the critical path, and D46 cleared it by reading the licence:*
`Fribb/anime-lists` has none, so vendoring was never permitted and an MIT-licensed dataset takes
its place. What that changes about this entry is the split — richer art is Phase 9b, the cached
AniList covers this entry says were already inside the MVP are Phase 9a, and only 9b ever needed
a dataset at all.

**Publishing an image is a one-way door, so it opens last.** Phase 13 builds the CI that pushes
to Docker Hub on a release tag; Phase 14 reviews §6's high-risk surfaces against the finished
application. The security pass runs first, and until it has, CI builds the image without
publishing it. Two things make this more than caution: Phase 11's migration squash stops being
free the moment somebody else's database exists, and a defect stops being local at the same
instant. Both clocks start on the first published tag, and neither can be wound back.

### D35 — Remote scoring is a source card, not a settings page

*Amended by D42. The card shape stated here survives and is why the Recommendations page still
reads as Sources does; what leaves it is the run started from it. Connection settings, the shared
sizes and the test remain on the card, and the schedule leaves for the single cadence of D40.*

*Amended again by D45, which reorders the two cards and marks this one experimental. The shape is
untouched; what changes is which route the page leads with, and that a scheduled remote run is now
something to switch on rather than something to switch off.*

*Withdraws the `/settings` page Phase 8 was to create. Phase 10 creates it instead.*

Phase 8 said it would build a dedicated `/settings` page holding only the model section, and
that Phase 10 would expand it. Declined. The Remote endpoint lives on the Recommendations page
as a card beside the manual one, in the shape Phase 5b already built for Sources.

**The precedent is stronger than the original plan's argument.** Sources holds one card per
source, each stating whether it is configured, each offering the action that proves it —
*Sync now*, or a file picker — and each hiding its settings behind a `Settings` disclosure. A
model endpoint is a source of rankings in every sense that matters: it is configured or it is
not, it either answers or it does not, and the person who wants to change it is looking at the
page where they use it. Sending them somewhere else to type a hostname is the friction D30
removed from importing.

**It does not take Phase 10's work away.** Phase 10's operator half is a *diagnostic register* —
"the page says where the file is and what is currently in effect, which is what makes a
misconfiguration diagnosable without shell access" — and it already planned to list the AniList
account there *while the Sources card also shows it*. Two jobs, not two controls: a card answers
"can this run right now", the register answers "what is in effect everywhere". The endpoint
appears in both for the same reason the account does.

**The page therefore has two cards and one shared one**, and the preview replaces them rather
than joining them. While a preview is on screen only the card that produced it renders,
collapsed to a heading and *Discard*, exactly as Sources does — a preview is one route's answer,
so it is one route's card, and two ways out for one preview was the bug that shape was written
to fix. Apply or discard is the only way back.

**What is shared is shared once.** How much to send — history size, titles to rank, rankings to
ask for — governs a run triggered by hand down either route, so it is lifted into its own card
beside where the title-language control sits, for the reason D30 gives: a setting drawn on two
cards is two controls over one value, and they drift.

*Consequence:* Phase 10 no longer expands a page; it creates one. Its content is unchanged.

### D36 — One home per setting

*Inverts D20's axis. Its conclusion — that a settings file and the database are different
stores and must not overlap — is kept.*

D20 split settings by **who owns the value**: operator configuration in `IConfiguration`, user
preference in the database. That line was drawn when the only settings a self-hoster needed
from outside were an account name and a kill switch. It does not survive contact with a page
whose first act is to type a hostname.

**The new line is what the value describes.** A setting that describes something outside
AniQueue — an integration, a deployment, somebody else's software — lives in `userconfig.json`
and is edited from the page that uses it. A setting that describes how a page looks to you lives
in the database. The key sets stay disjoint, which is the property D20 was actually protecting.

| `userconfig.json` | Database |
|---|---|
| Sync enabled, AniList account | Displayed title language |
| Per-source schedule, absence, conflict, primary † | Theme, date format |
| Model endpoint, model name, timeout | Default queue size |
| History size, titles to rank, rankings to ask for | Backlog default sort and filters |
| Personal notes in export, scoring schedule, batch size, staleness threshold | |

**† The per-source settings have not moved yet, and Phase 10 moves them.** 8a moved what
Phase 8 needs — the scoring settings, the AniList account and the kill switch — and left
`SourceSyncSettings` in the database. Two reasons, recorded so the gap reads as a decision:
moving it touches `SyncService`, `ImportService`, the Sources page and the seeder, which is a
sync refactor inside a scoring phase and would make a regression found in 8c hard to attribute;
and the entity is keyed `(ProfileId, Source)`, which a flat file cannot express, so the move has
to answer the one-profile question somewhere more load-bearing than scoring did. Phase 10 is the
settings phase and is already opening `ProfileSettings`, so it is where this belongs.

**Phase 10a pulls that chunk forward, ahead of Phase 15.** D40 writes the task toggles to the
file, and cannot while the entity still owns them — so the move happens first, on its own branch,
rather than inside a tasks phase where a sync regression would be hard to attribute. That is the
same argument this decision used to defer it out of Phase 8. The rest of Phase 10 is unaffected.

*Until then the Sources page writes an account to the file and a schedule to the database, in
one card.* That is the confusion this decision exists to end, and it is tolerable only because
nobody can see it: both are edited in the same disclosure, and neither says where it went.

**The application writes the file, and regenerates it whole.** D20 declined a UI that writes a
hand-editable file because comment-preserving round-tripping is not something
`System.Text.Json` does. That objection dissolves once the file is *generated* rather than
edited in place: AniQueue knows every key it accepts, so each save rewrites the whole document —
header, per-key comment, value — from the known set. Nothing is round-tripped, so nothing is
lost. The write is a temporary file and a rename, and a directory it cannot write to is a
reported failure rather than a fatal one, exactly as the template already is.

**A UI edit beating an environment variable is now correct rather than a hazard.** D20 kept the
template inert because a file read last would otherwise override a `Sync__AniList__UserName` set
in a compose file. That argument was premised on compose being the primary channel. It is not:
the compose file and the Unraid template carry container concerns — the `/data` mount and the
published port — and nothing else. A person who sets a value in the page expects the page to
win.

**Defaults are not a layer.** `appsettings.json` stops naming user-facing keys entirely; what a
key means when unset is the options class's own default. A default is not an override, and
keeping them out of the file chain is what stops this becoming four places to look.

**`Database:Path` remains the exception, and D20 already explained why:** the settings file is
found by looking beside the database, so a path set inside it could not be read until it was
already in use.

*Consequence:* the scoring sizes leave `ProfileSettings`, which drops four columns, and
`IRecommendationService` loses `GetOptionsAsync` and `SaveOptionsAsync` — the service stops
owning settings and takes them as an argument, which it already does everywhere else.

*Accepted knowingly:* a flat file cannot be per-profile, so the scoring settings assume one
profile. `Profile` and `ProfileSettings` stay in the schema untouched; multi-profile would have
to revisit this, and D-none records that multi-user is out of scope.

### D37 — A reply may be unwrapped, never reconstructed

*Amends Phase 7's stated parser behaviour and §6's rule against repairing AI content.*

Phase 7 said a reply "wrapped in prose, fenced in markdown, or missing a required field is
reported with what was wrong, and the user tries again or edits it by hand". The last clause is
the whole load-bearing part, and Phase 8 removes the person who was doing the editing. Fencing
is not an edge case — it is what a small model does most of the time, whatever the prompt says —
so taken literally the scheduled sweep would reject a correct answer, log it, and reject the same
answer identically on every tick thereafter.

**A `results` array is what identifies a candidate**, and extraction is mechanical rather than
hopeful because of it. The parser does not look for something JSON-shaped and hope: it offers
every `{` to `Utf8JsonReader`, which either reads one complete value from there or does not, and
keeps the ones shaped like a reply. A brace inside a title cannot start a candidate, because the
reader is inside a string when it reaches it. Text before, text after, and a fence around are
discarded.

*This decision first said the **envelope** was the identifier, and that was wrong.* 7a's parser
deliberately tolerates a missing envelope — "a model asked for JSON reliably returns the array it
was asked for and unreliably returns the wrapper around it" — so requiring one here would have
rejected exactly the replies 7a went out of its way to accept. Using the `results` array instead
means the extractor and the reader ask the same question about the same document, which is a
better property than the one that was intended.

**Where two candidates qualify, the last one wins.** This is not hypothetical: the prompt
contains a worked example carrying that exact shape, and a model that restates the question
before answering it produces two. A model's answer follows its preamble, so the last is the
answer — and a reasoning model that thinks out loud lands the same way.

**Everything discarded is reported.** A `ScoringProblem` at warning severity states how much
surrounding text was thrown away and whether an earlier matching object was ignored, so it
appears in the preview notice without blocking apply. That is what keeps §6's actual goal —
no score the user cannot account for — while accepting a messy reply.

**Three floors, and they are the difference between unwrapping and guessing.** Nothing without a
`results` array is a candidate, however JSON-shaped it looks — a server's own error object is not
a ranking. A ranking nested inside another object is not dug out, because reaching into a
structure for the part that looks right is reconstruction rather than unwrapping. And extraction
never relaxes what follows it: unknown ids, duplicate ids, rank collisions and out-of-range
values are rejected exactly as before.

**Structured output is asked for first, so this is a fallback rather than the path.**
`response_format` constrains the model at the source, and a constrained model cannot emit a
fence at all. It is on by default because the servers people actually run — LM Studio, Ollama,
llama.cpp — support it, and a server that does not answers with a clear error rather than
degrading quietly.

### D38 — The endpoint is a user setting, with guards

*Amends §6's "one fixed endpoint, held as a constant, never composed from user input" and D31's
position that the address must never come from a page.*

D31 kept the endpoint out of every browser-writable surface on the grounds that a server which
POSTs to an address a page supplies is a request-forgery gadget. D36 makes it a normal editable
setting, so that protection is replaced rather than merely dropped.

**What the exposure actually is, stated rather than assumed.** AniQueue has no authentication,
so anyone who can reach the port can already read the whole library. What a settable address
adds is reach into places the surrounding network cannot touch — loopback, the container
network, and cloud metadata endpoints. Phase 8's requirement to report what a failing endpoint
said turns that reach into a response oracle, and the two together are worth more than either
alone.

**Three guards, chosen because each blocks something the use case never needs:**

- The scheme must be `http` or `https`.
- `169.254.0.0/16` is refused. It is the only address in the picture that reaches something
  privileged, and no self-hosted model has ever lived there.
- Credentials in the URL are refused. `http://user:pass@host` exists to smuggle authentication
  somewhere and has no legitimate use here.

Loopback and private ranges are permitted, because that is exactly where a self-hosted model
lives, and refusing them would be theatre: reaching the page at all means already being on that
network.

**The diagnostic is capped and carries no transport detail.** What came back is shown to the
person debugging their own server — status line and the first 2 KB of body, encoded, never
rendered as markup — and headers, redirect chains and certificate detail are not. A truncated
body is everything a misconfiguration needs and very little that a scanner does.

*Honest limitation:* none of this is a boundary until Phase 12 exists. It is taken now because
it costs nothing at the point where nobody has an endpoint saved yet, and because Phase 14
should find a decision rather than an open question.

### D39 — Scoring re-runs when taste changes, not on a clock

*Amended by D40 in one place only: how often staleness is checked is now the single task cadence
rather than a scoring-specific schedule. The staleness rule below — N further titles rated — is
what decides the work and is untouched. D41 adds that a library change scores what is new without
re-scoring what has gone stale, which is what keeps an import from starting a full re-score.*

A ranking is only as good as the history it was anchored to, and that history grows. A title
dismissed at 4.2 against forty ratings may deserve 7.5 against three hundred, so re-scoring is
normal rather than exceptional — but a job that re-scores continuously spends someone's
electricity to produce numbers that differ only by noise.

**The trigger is accumulated ratings, and the interval emerges from it.** A title's score is
stale once **N further titles have been rated since it was scored**, default five. One rating is
noise; several is a changed picture. Nobody has to choose an interval, and the behaviour scales
itself: someone finishing four shows a week re-sweeps often, someone finishing one a month
rarely, someone on hiatus not at all.

**It costs one query rather than a per-row calculation.** The timestamp of the Nth most recent
rating is a single scalar; anything scored before it is stale. Combined with never-scored-first
ordering, that is the pick the job runs, and it selects nothing at all when nothing has changed —
which is what makes the job a genuine no-op rather than one that has to be told to stop (D25,
D28).

**A time floor was considered and declined.** Re-scoring against an unchanged history can only
produce noise, and "the numbers moved and nothing happened" is precisely what makes a
recommendation feature untrustworthy. The gap it leaves — someone who watches steadily but never
rates anything — is real and accepted; there is no new information in that case to re-score
against. Phase 9 supplies the natural extension: enrichment landing metadata or artwork on a
title *is* new information about that title, and is a reason to re-score it alone.

*A consequence worth stating, because it looks like a bug and is not:* a MyAnimeList import
lands hundreds of ratings at one timestamp, so everything scored beforehand goes stale at once.
That import genuinely changed everything the model knows, and the sweep works through it in
batches rather than all at once.

### D40 — Background work is a surface, not a side effect

*Amends `IBackgroundJob`'s "a second job gets a second typed table", replaces every schedule with
one, and deletes the reschedule-on-failure logic in both places it exists.*

Three background jobs run and none of them can be seen. `UnattendedSyncJob` reads a schedule set
on the Sources card, `ScoringSweepJob` reads one set on the Recommendations card, and
`RelationBackfillJob` has no setting anywhere and leaves no record at all. Nothing says what is
running now, nothing starts a run early, and nothing stops one.

**Whether that was tolerable is answered by the surfaces built to compensate for it.** A
*Refresh related titles* button on Sources, a last-synced line, a stalled banner, a coverage card
on Recommendations — each one a hole punched through a page to see a single job through it. D28
already recorded the failure mode in its own words: *an automatic job being operated by hand is a
design that has failed regardless of what its timer says.*

**Decision:** `/tasks` is where background work is seen and operated.

**A row is a schedulable unit, not a job.** `UnattendedSyncJob` loops over sources that have their
own enabled state and their own failure history, so one row for it would aggregate two of
everything and *Run now* would mean "whichever of these are due". The runner therefore iterates
units and calls the job once per unit — which is also what makes a per-source cancel expressible.
MyAnimeList never appears at all: `CanFetch` is false, nothing runs on its behalf, and a row whose
button is permanently disabled is a worse answer than no row.

**One cadence, and it is the only clock.** `SourceSyncSettings.Schedule` and
`ScoringOptions.Schedule` are deleted in favour of a single interval covering every task. Each job
still decides for itself whether it has anything to do — that is D25's gate and it is untouched —
but *when it is asked* is now one setting in one place. Two schedules that could disagree, on two
pages, for a single-user application reading one list and one model, was a control surface nobody
was using and a second thing to check when something had not run.

**Nothing reschedules itself.** `UnattendedSyncJob.BackoffMultiplier` doubled a source's interval
per consecutive failure to a cap of sixteen, and `BackgroundJobRunner` delayed its own loop on an
unhandled exception to the same cap. Both are deleted.

*The argument that lost is worth leaving legible, because it is a good one.* Backoff reasoned that
the failures worth backing off from — a rate limit, an outage, an account that cannot be read —
*none of them improve for being asked again on the dot.* True, and beside the point. What it costs
to ask again is one request. What it costs not to is a schedule the user chose being rewritten by
the application, invisibly, in response to a condition the user may already know about. A model
served from a machine that is switched on for a few hours a day fails most of the time **by
design**, and an application that answers that by stretching a daily check out to sixteen days
looks broken rather than patient. A user-defined schedule is respected, including while the task
is failing.

**Which requires a `JobRun` row for an unhandled exception**, not only for a handled one. Due-ness
is measured from the last run, so a job that throws before recording anything stays due and throws
again on the next tick, forever. Recording the throw advances the clock, and is what makes it safe
to delete the backoff that used to absorb it. The row carries a plain-words failure reason; the
exception and its stack stay in the log, because §6 forbids the latter reaching a page.

**`JobRun` is the record, and `SyncRun` stays exactly what it was.** This is the amendment to
`IBackgroundJob`'s doctrine, and it is a narrowing rather than a reversal: a typed table remains
where a job needs to *reason* about its own history — `SyncRun` still drives the Sources badges,
D21's held counts and the stalled banner — while `JobRun` holds only what every task has in
common, because that is what a page listing every task can render.

*The alternative was to union the typed tables at read time, and it does not survive SQLite.*
`SyncRun.StartedAt` and `RecommendationRun.CreatedAt` are both `DateTimeOffset`, which SQLite can
neither order nor compare, so a merged, paged, time-ordered history would have to be sorted in
memory over an unbounded set. One table ordered by `Id` has no such problem — the same workaround
`BuildRequestAsync` already uses.

**`RunAsync` returns what it did**, and the runner persists what it is handed. Computing the counts
a second time in the runner would create two records of one event that could disagree, which is the
objection `SourceSyncStatus.ConsecutiveFailures` already makes about a stored counter. Both jobs
already produce exactly this shape: `RelationBackfillResult` and `UnattendedSyncResult` existed
before this decision needed them.

**Runs that found nothing to do are recorded; ticks that were not due are not.** A converged task
and a broken task look identical if the page can only report the last run that changed something —
relations in its steady state legitimately does nothing for weeks. *"Checked forty minutes ago,
nothing to do"* is the single most reassuring line a task page has, and it costs one row per
cadence. Retention is the last two hundred runs per task, pruned on insert: five tasks, a thousand
rows, no cleaner and no policy setting.

**Cancel means skip this cycle.** It is cooperative and lands at the next safe point — between
batches, between requests, before a commit — and the button latches to *Stopping…*, as
`BusyDialog` already does for the same reason. A cancelled run writes a `JobRun` row and **no**
`SyncRun` row: nothing reached the library, so the library's audit trail has nothing to record, and
`SyncOutcome` needs no new value that would otherwise leak into `ConsecutiveFailures` and raise a
stalled banner over a button somebody pressed on purpose. Because due-ness reads `JobRun`, the
cancelled run advances the clock, and the task next runs when it was next going to.

*Mid-commit is safe, and this is why it can be stated rather than hoped:* `ImportService` opens an
explicit transaction, so a token tripped inside `SaveChangesAsync` rolls the whole thing back.
There is no half-applied sync.

**A toggle per row, written to `userconfig.json`.** D36 already places the per-source settings
there and Phase 10a moves them; the task toggles are the same values under a different button.
`Sync:Enabled` is deleted rather than kept above them — a global switch over a single per-source
switch, both in the same file, is one more thing to check and nothing D20's escape-hatch argument
needs, since the file is equally reachable either way. `Scoring:Enabled` is unchanged and simply
becomes that task's toggle. `Relations:Enabled` is new, and is a small reversal of *"there is no
decision to offer"*: there was none while the job was invisible, and a row carrying a button and no
switch invites the question.

**Failure is reported on the row, in plain words. A log file was declined.**
`SyncRun.FailureReason` already exists to be *"rendered to whoever opens the page"*, and replacing
*profile is private* with *failed — go and read a file* would be a regression dressed as a
simplification. Behind that line the log is stdout and nothing else: the container runtime captures
it and every layer above Docker reads it from there, while a file written inside the container is
invisible to all of them. Declining it also declines a §12 dependency approval, since
`Microsoft.Extensions.Logging` ships no file provider and the shared framework has none either.
Phase 11's README documents `docker logs` and the `max-size` / `max-file` options an operator
should set, because the default `json-file` driver does not rotate.

### D41 — A job announces what it changed; nothing announces what to run next

*Extends D28's broadcast from the two sync entry points to every job, and adds the rule that
decides how much work a wake-up is worth.*

D28 gave the runner a second wake source so a sync's commits did not sit unenriched until the next
tick, and it holds for exactly one hop. The second hop has no signal: relations writes edges, and
the metadata job that wants them discovers them on its own timer. At a fifteen-minute tick that is
invisible. Under D40's single relaxed cadence it is a day per link, so the three-job chain D25
describes converges in three days.

**Decision:** every job publishes when it changed something, and no job names another.

**This is D28's mechanism rather than a new one.** The signal remains *the data changed*, never
*run X next*, so D28's own test still passes: delete every broadcast and everything still
converges, one cadence later. A job disabled, rerun or replaced affects nothing downstream except
how soon it notices, which is what D25 asked for and what an orchestrator would take away.

**Explicit chaining was considered and declined.** A sync that enqueued relations, which enqueued
metadata, would give the same immediacy and a causal chain legible in the history. It also makes
every job know what follows it, so disabling metadata silently breaks artwork — the coupling D25
was written against. The legibility is recoverable without the coupling: `JobRun` records the
trigger, so the history says *woken by a library change* rather than leaving it to be inferred.

**A wake-up is not the same as a due run**, and conflating them is how a ten-second import turns
into hours of somebody's GPU:

| Trigger | Due-ness | Work selected |
|---|---|---|
| `Timer` | the cadence must have elapsed | new and stale |
| `Manual` | skipped — the user is the timer | new and stale |
| `LibraryChange` | skipped | new only |

*The case that rule fixes is already documented in D39:* a MyAnimeList import lands hundreds of
ratings at one timestamp, so every score taken before it goes stale at once. Under a bare broadcast
that import immediately starts an unattended re-score of the whole back catalogue. Under this rule
it scores what is new, and the re-score waits for the cadence, when nobody is standing over it. The
two populations are already separated by `GetCoverageAsync`, which reports `Unranked` and `Stale`
apart, so nothing new is computed to tell them apart.

*And a manual run is a cadence check brought forward* — not a bigger run, and not a different one.
That is what makes *Run now* safe to press: it cannot do work a scheduled run would not have done
anyway, only sooner.

**Terminating is structural.** A job publishes only when it changed something, exactly as
`UnattendedSyncJob` already gates on `result.ChangedLibrary`, and a job woken with nothing to do
changes nothing and therefore says nothing. Publish unconditionally and the jobs wake each other in
a ring, each finding nothing; that is the one discipline this decision depends on.

**The payload is optional, and Phase 15a settled which way.** `LibraryChange` is sync-shaped —
`Source`, `Created`, `Updated`, `SlotsReleased`, `AbsentFlagged` — and this decision left open
whether it should generalise to describe relations and scoring, or degrade to a bare signal. It
degrades, on evidence that was already in the code: `BackgroundJobRunner` has *always* discarded
the payload, and `StaleLibraryNotice` is the only thing that reads it. Generalising would have
made every job invent counts for a listener that ignores them, and grown the notice a sentence per
kind of work. So a job with nothing a page could usefully say publishes nothing, the notice stays
quiet, and every runner still wakes.

**A producer must not treat the signal as a reason to run**, which 15a found by building it. Sync
publishes when it commits, and every runner including its own hears that — so a sync that bypassed
its cadence on a library change would schedule its own next run, forever. The rule is for jobs
*consuming* the signal; sync honours its cadence for everything except a manual run. It is a real
limit on how far "no job knows what any other does" can be pushed: a job has to know whether it is
upstream or downstream of the thing it is hearing about.

### D42 — The model is only ever asked with nobody waiting

*Withdraws 8c's interactive remote run, and with it the decisions built to make waiting bearable.*

8c put a *Rank now* button on the Recommendations card and worked hard on the wait behind it:
elapsed time with the previous run's duration for scale, a cancel that `BusyDialog` gained a
parameter to support, and a soft guard for requests too large to plausibly come back. All of it
correct, and all of it in service of a person sitting in front of a modal for ten minutes while a
self-hosted model thinks. That is not a workable primary flow for the application's main work, and
the two routes that do not require it — the copy-and-paste exchange and the scheduled sweep — are
sufficient between them.

**Decision:** a ranking arrives either by hand through the paste route, or from the sweep with
nobody present. AniQueue never opens an outbound scoring request that somebody is waiting on.

**`IScoringGate` is deleted, and this is what it was for.** Its whole purpose was deciding which of
two claimants on a single-request-at-a-time model yields, and D39 settled it as *the person wins,
and the sweep loses nothing*. Remove the person and `EnterInteractiveAsync` has no callers;
`EnterSweepAsync` has none either, because a sweep and a *Run now* both go through the same
`BackgroundJobRunner` loop, which is sequential, so two sweeps cannot overlap by construction. The
interface, its implementation, its tests and the sweep's between-batch stand-down all go with it.

**Test connection survives, and yields by interface rather than by lock.** It is the one outbound
call left outside the runner, and firing it into a sweep batch would queue behind it and look like
a timeout. The task registry already knows the scoring task is running, so the button is disabled
while it is and says why. That is a smaller mechanism than a gate and a more honest one, because
the user learns the model is busy rather than watching a test hang.

**What is kept is everything local.** `BusyDialog` and `BusyScope` survive and still cover
`PreviewAsync`, `ApplyAsync`, `BuildAsync`, the settings saves and the test — an apply is several
hundred rows reporting real progress, and `BusyScope`'s show-delay means a small one never raises a
dialog at all. What `BusyDialog` loses is its `OnCancel` parameter: its own comment records that
the remote run was the first operation here where stopping was free, and it was also the last.

**`RecommendationRun.DurationMilliseconds` is kept and its reasoning replaced.** It was argued for
as something to show *while a person waits on a request with no progress to report*. Nobody waits
now. The sweep still measures it and *Past rankings* still shows it, and what it answers — how long
the model took — is worth knowing after the fact as well as during.

---

### D43 — The model is asked for a score, not for an order

*Withdraws `rank` from the scoring interchange, and falsifies 8d's argument that chunking is
harmless.*

7a's schema asks for four numbers per candidate: an `id`, a `rank`, a `predictedScore` and a
`confidence`, with a `reason` beside them. Two of those turned out to be requests a model could
not honestly satisfy, and in both cases it satisfied them anyway. They were found within an hour
of each other while watching real replies from a local model, which is the only way either could
have been found: both produce well-formed JSON that the parser accepts.

**The reasons were invented, and the prompt asked for it.** The instruction read *"Say why in one short sentence, referring to their history where you can"*,
which offers no way to decline. Told to cite the history and finding nothing close, two different
models manufactured a citation — and the nearest number to hand was the score they had just
produced:

| model | reason returned | its own `predictedScore` |
|---|---|---|
| `qwen3-vl-8b` | "You rated 'Haite Kudasai, Takamine-san' 6.0." — an **unwatched** candidate | 6.0 |
| `gpt-oss-20b` | "Sci-fi thriller like Psycho-Pass you rated 7." — about *Psycho-Pass* itself | 7 |

**The payload was never wrong**, which is what made this worth chasing rather than dismissing.
`history` is completed titles carrying the user's own score; `ScoringCandidate` has no score field
at all. There was nothing to misread and no leak to plug. What was wrong was what the prompt asked
for.

**The worked example was the stronger instruction.** It read `"predictedScore": 9.5` beside
`"reason": "Same director as one you rated 9."` — a rating inside a sentence, one decimal from the
score, attached to no title anybody could check. A small model reproduces an example far more
faithfully than it obeys prose, so the example taught precisely what the prose was later asked to
forbid. Both are fixed together: three rules where there was one, an escape hatch for when nothing
in the history is close, and example reasons carrying no number at all.

*Fixed ahead of this decision as a change of its own, because a prompt correction carrying tests is
not a design change and did not need one.* What it does not do is make the scores trustworthy, which is
the other half.

**The scores were positional, and that is the serious half.** Three consecutive batches from the **same model**, `gpt-oss-20b`, at one setting, `predictedScore`
in reply order:

| batch | scores |
|---|---|
| A | `10 8 8 8 8 8 8 7 7 7 7 7 6 6 6 6 6 6 6 6 6 5 5 5 5` |
| B | `9 9 9 9 8.5 8 8 7.5 7.5 7.5 7 7 6.5 6 6 5.5 5 4.5 4` |
| C | `9.2 8.5 8.0 6.5 7.0 6.8 7.5 6.9 7.2 6.7 5.8 6.2 7.8 5.5 6.4 8.3 7.6 6.9 5.9 7.4 …` |

A is a staircase: coarse integer buckets descending monotonically with rank, which is a model
ordering the batch and then back-filling a score to match its position. C is independent scoring —
one decimal, non-monotonic, position 4 scoring below position 5. B is A for nineteen results and
then stops. **One model, one setting, three different methods**, so this is not a capability
ceiling that a better model fixes.

**This falsifies 8d.** That section argued:

> `rank` is placement within a batch and never leaves it; `predictedScore` is a prediction against
> the user's history, which every batch carries identically … so the batch-relative number is never
> what is compared across the library.

The first clause still holds. `Rank` is stored on `RecommendationRunItem` and never reaches
`LibraryEntry`; the backlog orders by `RecommendationScore`. What fails is the second. When a batch
ladders, the top title scores 10 and the bottom scores 5 **because of where they sat in that
batch** — so batch A's `8` and batch C's `8.0` are measuring different things, and the backlog
sorts them against each other as though they were not. A strong title in a strong batch lands below
a weak title in a weak batch, and nothing downstream can tell the two scales apart afterwards.

The corruption is indirect, which is why 8d missed it: rank does not leave the batch, but the score
derived from it does. 8d's remaining claim — that what is left is calibration drift, a difference
of degree — is only true of batch C. Of batch A it is a difference of kind.

**Decision: stop asking for a rank.** The request asks for a score and a confidence; the reply carries no
ordering. Where an order is needed — the preview, a run's stored items — it is taken from
`predictedScore` descending, which is what the backlog already sorts by. The one number the whole
feature rests on becomes the only number anything reads.

**A ranking is not needed to produce a score, and batch C is the proof.** The same model, asked the
same question, scored each title on its own merits with no relationship to the order it happened to
return them in. What asking for a rank adds is an invitation to derive one number from the other,
which roughly half of the observed batches accepted.

**This is not a token argument.** Dropping `rank` saves on the order of a hundred output tokens a
batch, which is real and would never have justified the change. It is dropped because it corrupts
the score.

**What is knowingly lost.** The parser's duplicate-rank and gap checks go with the field, and they
were doing real work — they rejected batches for a repeated rank and for ranks running 1–17 across
16 results. Every check that matters to a *score* survives: an unknown id, a duplicate id, a score
off the scale, a confidence outside 0–1. What is given up is the detection of a malformed
*ordering*, which stops being worth detecting once nothing asks for one.

**Stored ranks go too, rather than being left unread.** The alternative — keep the column, stop
populating it — leaves a number in the database that no code writes and no surface shows, which is
a question every future contributor has to answer before they can rule it out. 6a took the same
view of the franchise columns and dropped them in the same migration that removed their last
reader, rather than leaving them behind as data nobody could account for. Historical runs lose
their placement and keep their scores, which is the half anything ever read.

**What this does not claim to fix.** Removing the invitation is not the same as guaranteeing
independent scoring: a model may still anchor a batch to itself, and calibration drift between
batches remains real. This is a mechanism removed, not a property proved, and the check is to run a
sweep against a real model afterwards and look at whether the staircase is gone.

---

### D44 — The model chooses the comparison; AniQueue states the fact

*Makes the reason's factual content checkable, after three attempts to make it trustworthy by
asking nicely.*

**The reason is the last field accepted on trust.** Every number in a reply is validated — against
a schema, a scale, a range — because D31 holds that an instruction is a request rather than a
guarantee. The sentence explaining those numbers gets none of that. `ScoringResponseParser` trims
it, truncates it past 500 characters, and passes it through; the backlog then prints it to the user
under *"Why this score"* as a statement about their own viewing history.

**D43's prompt fix reduced the problem and did not close it.** Three rules replaced one, an escape
hatch was added for when nothing in the history is close, and the worked example was rewritten to
carry no number. All three were verified as being sent. `gpt-oss-20b` then returned, against a
library where every one of these titles is `Planning` and unrated:

| reason returned | the truth |
|---|---|
| "Similar to IS: Infinite Stratos which you rated 10" | never watched, never rated |
| "You enjoyed the first season of The 100 Girlfriends… (8), so the second season should be similar" | S1 was an **unwatched candidate in the same batch** |

The second one violates a rule that was in the prompt at the time, verbatim: *"Everything in
`candidates` is unwatched: never say they rated one."*

**So stop trying to make the sentence trustworthy and make the claim unnecessary.** The model is
asked for a comparison it may make freely, and forbidden from asserting the facts about it.
AniQueue holds those facts — it sent the history — and writes them itself:

```json
{ "id": 412, "predictedScore": 8.5, "confidence": 0.8,
  "basedOn": ["Nichijou", "KonoSuba"],
  "reason": "Comedy with a strong ensemble." }
```

rendered as *"Comedy with a strong ensemble. Compared against **Nichijou** (9) and **KonoSuba**
(8)."* — where the two scores come from the database rather than from the model. A fabricated
rating stops being something to detect, because there is no field in which to write one.

**`basedOn` is a list, and that is the whole of the design rather than a detail.** The obvious
version of this — one cited title per result — was rejected on an objection raised while reviewing
it: *does a single citation invite comparison against one arbitrary show, rather than against the
whole scored backlog?* It does, and the concern is D43's own failure wearing different clothes.
Asking for a field changes how the other fields are produced, so requiring exactly one comparable
invites a model to find one plausible title and score by analogy to it — which is a worse
prediction than weighing five hundred and fifty rated titles, and one that looks identical in the
reply. The existing prompt already refuses this trap in prose, offering *"name a title from it, **or
describe a pattern across it**"*, and a single-valued field would quietly delete the second half.

The list is therefore genuinely 0..n, and each arity means something:

- **empty** — nothing in the history is close, which the prompt already invites a model to say and
  which is a legitimate and useful answer;
- **one** — a direct comparable;
- **several** — a pattern, which is the honest shape when taste is diffuse and is the case a
  single-valued field would have lost.

**Score first, citation after.** In the emitted shape `predictedScore` precedes `basedOn`, so the
score is committed before the comparison is named and the citation cannot have driven it. This is
D43's lesson applied in advance rather than after a sweep goes wrong. It buys less than it appears
to — a citation written after a score is a post-hoc rationalisation, which is arguably what every
model explanation is — but it costs nothing and it removes the mechanism D43 was bitten by.

**Validation, and it is ordinary.** Every `basedOn` entry must match, exactly, a history title sent
in *this* request. AniQueue sent those strings, so the comparison is a set lookup and needs no
fuzzy matching and no new payload: history entries deliberately carry no identifier (D-none; see
`ScoringHistoryEntry`, where ids were left out to keep the largest part of the payload small), and
the title is sufficient because it is the same string travelling back. An unmatched entry, or a
digit appearing anywhere in `reason`, drops the reason and keeps the score — a bad sentence must
never cost a good number, which is the same proportionality D31 applies to a long reason.

**What this does not claim to fix.** Verification proves a cited title was rated and that the score
shown against it is real. It cannot prove the comparison is *apt*: a model may cite *Gunbuster*
against a slice-of-life comedy, and every fact rendered will be correct. This closes false
statements about the user's own history, which is the class that has actually occurred and the
class a user cannot check without opening their own list. It does not make the reasoning good.

**Keeping the prose is a requirement, not an assumption.** A cheaper route exists — drop `reason`
entirely, score with numbers alone, and fit far more per batch — and it is rejected: the
explanation is a large part of why this feature is worth having, and a score with no account of
itself is the black box the whole workflow was built to avoid. `basedOn` is chosen partly because
it is the option that makes the reason *more* useful rather than less.

**Open, and to be settled by measurement.** Whether requiring a citation shifts the scores is the
same question D43 had to answer about rank, and it takes the same answer: run a sweep with and
without against a real model and compare the score distributions. If citation turns out to distort
prediction the way placement did, the field becomes optional rather than required, and a result
carrying no `basedOn` keeps a numberless reason.

---

### D45 — The manual route leads; the remote one is experimental and opt-in

*Amends D35's ordering and reverses the default of `Scoring:Enabled`. Nothing about the contract
changes — this is an admission about how well it works.*

**Three models, one library, one result each.** Tried against the same real backlog of 564 rated
titles, through the same request and the same schema:

| model | outcome |
|---|---|
| `openai/gpt-oss-20b` | answers, at 564 rated titles |
| `google/gemma-4-12b` | no answer, even with the history cut to **50** |
| `qwen3.5` | no answer at 564 |

The two failures share a shape and it is not a bug in the request. Both spend their entire output
allowance reasoning and never begin the JSON — gemma enumerating the rating history back to itself
in prose, run after run, until the budget is gone. That is a disposition, not a defect, and no
setting on this side reaches it. Raising the ceiling gives such a model more room to ramble;
cutting the history gives it less to recite and did not save gemma at a fiftieth of the original.

**So the honest thing is to say so on the page rather than to keep tuning.** The alternative —
chasing a fix per model — is unbounded work against a population AniQueue cannot enumerate, for a
route that is already optional.

**Three changes, and the third is the one that matters.**

*Manual comes first.* It works with every model, because a person reads the reply before it is
applied, and the failure mode is a wasted paste rather than a silent no-op. A page should lead
with the route that works.

*The remote card is badged experimental*, in amber rather than red: the feature is not broken, it
merely does not work with everything, and red would send somebody hunting for a fault on this side.

*A scheduled remote run is opt-in.* `Scoring:Enabled` defaults to false.

**No new setting, and that is deliberate.** `Scoring:Enabled` already gated `ScoringSweepJob` and
nothing else — the manual paste route never read it — so it already meant "scheduled remote
ranking" whatever its documentation claimed. Its own comment, *"refuses every run, scheduled or
pressed"*, stopped being true when D42 deleted the pressed run. Adding a second switch beside it
would have been two controls over one behaviour, which is exactly what D30 forbids; flipping the
default of the one that already existed is the whole of the mechanism.

**What this costs.** An existing installation with the key written to `true` is untouched, because
a value in the file beats a default — so this reaches new installations and anybody who has never
set it. Somebody with a model that works has to turn it on once, having read why it is off. That
is the same bargain `Tasks:Schedule` already makes, and for the same reason: unattended work that
spends the user's electricity is a thing to opt into.

**Revisited when there is evidence to revisit it with.** Three models is enough to stop promising
the route works and not enough to characterise which models do. The badge comes off when a wider
sample says something more useful than "it depends".

**Phase 17 holds what was found while establishing this**, including several things that are wrong
today and are not urgent only because this decision made the route opt-in. It is written rather
than built for the same reason the badge is on: the right answer to most of it depends on what a
wider sample of models turns out to look like.

### D46 — A dataset with no licence is not a dataset AniQueue can use

*Reads the licence D25 filed as unread, which has blocked Phase 9 since it was written, and
replaces the dataset rather than the plan.*

**The blocker turned out to be worse than "unread", and that is what settles it.** Three datasets
were checked, and the one this file has named since §10 was written is the one that cannot be
used at all:

| Dataset | Licence | TVDB | TMDB |
|---|---|---|---|
| `Fribb/anime-lists` | **none** — no `LICENSE` file, GitHub reports `license: null` | yes | yes |
| `Anime-Lists/anime-lists` | **none** | yes | — |
| `manami-project/anime-offline-database` | ODbL 1.0 + DbCL 1.0 | — | — |
| `Kometa-Team/Anime-IDs` | **MIT** | yes | — |

No licence means all rights reserved, so vendoring Fribb was never on the table and neither was
anything else. The question D25 left open — vendor or fetch at runtime — is therefore answered by
having no legitimate first option, and the honest reading is that runtime fetching would have been
unlicensed use rather than permitted use. `manami-project` is the one carrying an explicit,
redistributable licence and it is the one that solves nothing here: it publishes MAL, AniDB,
AniList and Kitsu cross-references and no TVDB or TMDB at all. Fribb's value *was* the merge with
the unlicensed AniDB↔TVDB list, and that is exactly the part with no permission attached.

**`Kometa-Team/Anime-IDs` is MIT, and small.** 1.6 MB against Fribb's 7.5 MB, 16,883 records keyed
by AniDB id, rebuilt daily. MIT permits vendoring outright, so the redistribution argument that
has held this phase up simply stops applying — and the choice is still to cache under `/data`
rather than commit it, for §10's other reason: a file re-committed on every refresh is permanent
history, and it goes stale for exactly the new titles a user is most likely to be planning.

**What it costs is the TMDB column.** The record carries `tvdb_id`, `tvdb_season`,
`tvdb_epoffset`, `mal_id`, `anilist_id` and `imdb_id` — no TMDB anywhere. Measured against the
same library §10 measured Fribb against, now 810 titles, every one of them carrying an AniList id:

| Format | Titles | In dataset | → TVDB | → IMDb |
|---|---|---|---|---|
| TV | 322 | 313 | **311 (97%)** | 4 |
| OVA | 225 | 168 | 141 (63%) | 26 |
| MOVIE | 159 | 141 | 75 (47%) | **126 (79%)** |
| SPECIAL | 72 | 12 | **11 (15%)** | 5 |
| ONA | 31 | 21 | 20 (65%) | 0 |
| **All** | **810** | 656 (81%) | **559 (69%)** | 161 (20%) |

**It is better than Fribb where it matters and worse where it does not.** Fribb knew 95% of the
library and keyed 66% to TVDB; Kometa knows 81% and keys 69%. The presence gap is concentrated in
`SPECIAL`, which was already the format nothing could map — 15% here against Fribb's 16%, so
almost none of that 14-point difference is art anybody would have seen. Series, which is where
richer art is worth having, go from 98% to 97%.

**Films are the real gap, and IMDb closes most of it.** 47% of films key to TVDB, against Fribb's
88% to TMDB — but 79% carry an IMDb id, and TMDB's `find` endpoint resolves an IMDb id to a TMDB
film exactly rather than by matching titles. So film art is reachable at 79% for one extra request
per film, against 88% for none. That is the whole price of the licence being readable.

**The season warning survives intact.** `tvdb_season` and `tvdb_epoffset` are on the record, which
is precisely what D25's first schema warning said a mapping must carry — a TVDB id without the
season it refers to claims an identity it does not have.

*Confidence, stated plainly:* the licences and the coverage figures above are measured, the latter
against the development library rather than a general population, and §10's own warning applies
unchanged — coverage tracks format, so a library shaped differently will land somewhere else.
**The TMDB, TVDB and fanart.tv API terms, key requirements and rate limits are still unverified**
and must be checked before any of them is committed to; nothing about this entry changes that, and
none of it blocks 9a.

### D47 — AniQueue may fetch an image, but only from an address it already knew

*Builds the artwork half D25 predicted, and answers the question it did not ask: what a
self-hosted application is allowed to reach on its own initiative.*

**Phase 9 splits.** `9a` caches AniList covers and renders them; `9b` maps ids through D46's
dataset and layers richer art over that base. The split is not for size. 9a needs no external
dataset and no licence, so it can ship while 9b's API terms are still unverified — and it is the
half carrying §10's actual argument, which is about a wall of text on the backlog rather than
about a cache.

**Caching art nothing renders would have been the wrong phase.** Phase 9's exit criterion as
written ended at "ids mapped and art cached under `/data`", which is a cache with no consumer and
the speculative infrastructure D11 argues against. 9a ends on a page instead: a thumbnail column on
the backlog and on Up Next, with the colour block D25 already banked as the fallback.

**§6's outbound-HTTP rule stops being true as written, and gets narrowed rather than excepted.**
That paragraph says every endpoint AniQueue reaches on its own initiative is a constant held in
code, with the scoring endpoint as the single exception. A cover URL arrives inside an AniList API
response, so it is neither a constant nor user input. The answer is that **the host set becomes the
constant and only the path comes from data**: every one of the 810 covers in the development
library is on `s4.anilist.co` over https, an allowlist costs nothing to hold in code, and 9b
extends it rather than abandoning it. On top of that the fetch follows no redirects, requires an
image content type, caps the response as §6 caps an upload, and times out. A URL failing any of
those is a permanent failure on its row, not a retry — which keeps the rule's property intact
instead of adding a second exception to a rule written as having one.

**`Anime.CoverImageUrl` is dropped, and the reason is a bug rather than tidiness.** D25 predicted
the column would not survive a second image kind. It does not survive the *first* one either: the
column holds AniList's `extraLarge` URL and is written through the import merge, which preserves
an existing value rather than overwriting it. Repointing the parser at a smaller size would have
given the right URL to titles arriving afterwards and left every existing row holding the old one
— an entire library quietly cached at nine times the intended size, visible in no build and no
test. `AnimeImage` owns `RemoteUrl` instead, keyed `(AnimeId, Kind, Source)`, and inserting a
missing image row is not the same operation as overwriting a scalar, so the next sync fills every
existing title without touching the field-preservation rules D18 and D21 depend on.

**The size stored was the wrong one by a factor of nine, measured.** AniList publishes three, and
the same title costs:

| Field | bytes | 810 titles | a 50-row page |
|---|---|---|---|
| `medium` | **9.7 KB** | 16 MB *(measured)* | 486 KB |
| `large` | 28.4 KB | 23 MB | 1.4 MB |
| `extraLarge` | 83.3 KB | 67 MB | **4.2 MB** |

9a caches `medium`, which is ample for a 40×60 slot at 2×.

**The whole-library column is measured rather than multiplied, and the first estimate was half the
truth.** Extrapolating one JPEG gave 7.9 MB; the real cache is **16 MB**, because 223 of the 810
covers are PNGs averaging 30 KB against a JPEG's 11 KB at the same dimensions. It changes no
decision — 16 MB is still about four times the database rather than the twenty `extraLarge` would
have been, and D33 makes `/data` the backup, so that ratio is somebody else's copy time — but a
number arrived at by multiplying one sample is worth marking as such wherever one appears.

Server-side downscaling was rejected without argument: it means ImageSharp or
SkiaSharp, which is a §12 dependency decision and native libraries in the container, to save
bytes that AniList is already serving in the right size. A later layout wanting larger art re-runs
the job against a different URL; the row stores its own, so that is a re-run and not a migration.

**Four smaller rules, decided once.**

- **Immutable URLs.** Art is served from `/covers/{id}/{hash}` with a year's `max-age` and
  `immutable`. AniList's URLs carry a content hash, so replaced art changes URL, refetches, and
  arrives at the browser under a new address — no revalidation, and never a stale poster. The page
  already joins `AnimeImage` to know whether to render an image or a colour block, so the hash is
  a column on a join it is doing anyway.
- **Disk wins.** The job's precondition is "row says cached *and* the file is there", so deleting
  the covers directory to reclaim space heals within a tick instead of breaking every image
  permanently. The same pass deletes files with no row, which is what removes art for a title that
  has left the library.
- **Two failure classes.** A 404, a non-image content type, an oversized body or a disallowed host
  is permanent and never retried; a timeout, 5xx, 429 or connection failure counts against five
  attempts, and a 429 slows the rest of the pass. Both clear the moment `RemoteUrl` changes, so
  replaced art is always tried again. This is the bound D40 removed from the runner arriving where
  it belongs — on the row, not in a job rescheduling itself.
- **Silently, still.** D25's rule is unchanged: a failed cover logs and shows nothing. What a user
  needs is on the task's own row (D40), which reports what the pass fetched and what it could not.

**What this costs, measured rather than estimated.** One migration, one job, one endpoint and a
component; **16 MB** under `/data` and 810 one-off requests against a CDN already serving this
library's covers to its owner, which took **4 minutes 6 seconds**. A first run on a large library
therefore shows colour blocks for a few minutes before it shows posters, which is exactly the
degradation `coverImage.color` was banked for in Phase 6.

---

## 3. Solution structure

Follows the brief's §36. It is a sensible shape; no argument.

```
AniQueue.slnx
Directory.Build.props / Directory.Packages.props
.editorconfig / .gitattributes / .gitignore / .dockerignore
Dockerfile / docker-compose.yml
README.md
docs/ROADMAP.md, docs/BUILD-PROMPT.md

src/
  AniQueue.Core/            entities, enums, DTOs, interfaces, pure logic. No EF, no Blazor.
  AniQueue.Infrastructure/  EF Core, SQLite, migrations, service implementations
  AniQueue.Web/             Blazor components, DI composition, configuration, hosting

tests/
  AniQueue.Core.Tests/            pure, fast, no database
  AniQueue.Infrastructure.Tests/  SQLite in-memory, real EF
```

**The split that makes the test plan work.** Pure computation lives in Core and is tested
with no database: MAL XML parsing, runtime maths, hybrid ranking, AI-result validation.
Anything touching data lives in Infrastructure. Core has no EF reference not for
architectural purity but so most of the suite runs in milliseconds with no fixtures.

Interfaces are declared in Core, implementations in Infrastructure. Blazor components call
services only — no `DbContext` in `.razor`, no LINQ in markup, no business logic in
components. No HTTP API between the Blazor server and itself.

---

## 4. Domain model

```mermaid
erDiagram
    Profile ||--o{ LibraryEntry : owns
    Profile ||--o{ QueueItem : owns
    Profile ||--|| ProfileSettings : has
    Profile ||--o{ RecommendationRun : owns
    Profile ||--o{ SourceSyncSettings : configures
    Profile ||--o{ SyncRun : records
    Anime ||--o{ LibraryEntry : "referenced by"
    Anime ||--o{ AnimeExternalId : "identified by"
    Anime ||--o{ AnimeImage : "pictured by"
    Anime ||--o| QueueItem : "queued as"
    RecommendationRun ||--o{ RecommendationRunItem : contains
```

### Anime

`Id, Title, TitleRomaji?, TitleEnglish?, TitleNative?, MediaType, EpisodeCount?, EpisodeDurationMinutes?,
ReleaseYear?, StartDate?, CoverImageColor?, Description?, Source, CreatedAt, UpdatedAt`

- `MediaType`: `Unknown, Tv, Movie, Ova, Ona, Special, Music`
- `Source`: `Manual, MyAnimeList, AniList` — **provenance only** since D17. Identity lives on
  `AnimeExternalId`.
- **Nothing here records grouping**, and nothing will (D23, D24). Relations are stored as edges
  between external identifiers; there is no membership column and no group to be a member of.
- `Title` is the **resolved display title**, and the only one anything else reads — the backlog
  searches, sorts and pages on it in SQL. The three variants beside it each know their own
  language, which is what lets the preference be switched without a sync (D22); they are null for
  manual and MyAnimeList-only rows, which have one name and keep it.
- `EpisodeDurationMinutes` and `ReleaseYear` are first populated in Phase 5b. A MyAnimeList
  export carries neither, so before that phase they are null on every imported row and every
  runtime and decade feature in Phase 3 is inert by design.
- **`CoverImageUrl` is gone** (D47). Where a title's art lives is a fact about an image, and a
  title has more than one, so it is a row on `AnimeImage` rather than a column here.
  `CoverImageColor` stays beside it: six bytes describing the title rather than an address, and
  what renders while no image has been cached yet.
- Domain entities are never coupled to MAL/AniList DTOs.

### AnimeExternalId

`Id, AnimeId, Source, ExternalId`. See D17. Unique on `(Source, ExternalId)` — **unfiltered**,
because a manual entry has no rows rather than a null identifier. A title carries zero or more,
which is what lets an AniList sync bridge onto a MyAnimeList-imported row through `Media.idMal`
instead of conflicting with it.

### AnimeImage

`Id, AnimeId, Kind, Source, RemoteUrl, ContentHash?, FetchedUrl?, FileExtension?, ByteCount?,
FetchedAt?, FailedAt?, FailureIsPermanent, AttemptCount`. See D47. Unique on
`(AnimeId, Kind, Source)`.

- `Kind`: `Poster, Banner, ClearLogo, Backdrop` — 9a writes only `Poster`, 9b adds the rest.
  Stored as an integer, so the values are a data contract and append-only.
- `Source` reuses the existing enum for now and gains TVDB and TMDB in 9b.
- **The bytes are not here.** §6 forbids image binaries in the database; the file lives under
  `<data>/covers/` and this row records where it came from, whether it arrived, and what to serve
  it as. `ContentHash` is null until it has, and is what makes the served URL immutable.
- **`RemoteUrl` is the invalidation key.** AniList's URLs carry a content hash, so replaced art
  changes the URL — which clears both failure states and re-fetches, with nothing scheduled and
  no timestamp compared.
- **`FetchedUrl` is what makes that free of a gap.** One column is the picture that should be
  shown and the other is the picture being shown; outstanding work is the two disagreeing, which
  covers "never fetched" and "replaced since" with one comparison, and the old art keeps
  rendering until the new art has actually arrived.

### LibraryEntry

`Id, ProfileId, AnimeId, Status, UserScore?, EpisodesWatched, DateStarted?, DateCompleted?,
DateAdded, LastUpdated, PersonalNotes?, IsHidden, LastWrittenBySource?, RecommendationScore?,
RecommendationConfidence?, RecommendationReason?, RecommendationUpdatedAt?`

- `Status`: `Planning, Watching, Completed, OnHold, Dropped`
- Unique `(ProfileId, AnimeId)`; indexes on `(ProfileId, Status)`, `(ProfileId, IsHidden)`
- `UserScore` is 1–10 or null, enforced by `CK_LibraryEntries_UserScoreRange`. Sources that use
  a different scale must normalise *before* reaching here — see Phase 5b on AniList's five
  scoring systems.
- `LastWrittenBySource` records who last wrote the tracking fields, so D18's precedence can
  tell a stale source from an authoritative one. Null on rows written before that decision.
- No `QueuePosition` — see D1. No `ManualPriority` — see D14.

### SourceSyncSettings

`Id, ProfileId, Source, IsEnabled, PrecedenceRank, PollIntervalMinutes, ApplyUnattended,
ConflictPolicy, AbsencePolicy, LastWatermark?`

Keyed `(ProfileId, Source)`, which is why these are not on `ProfileSettings` (D20). Holds
D18's precedence rank, D19's absence policy and D21's application and conflict policies. The
account identifier is **not** here — it is operator configuration (D20).

> **Deleted by Phase 10a.** D36 places every one of these in `userconfig.json`, and D40 depends
> on the move because the task toggles are written there. The precedence rank becomes a single
> key naming the primary source rather than an integer per row.

### JobRun

`Id, TaskKey, UnitKey?, Trigger, StartedAt, FinishedAt, Outcome, ItemsProcessed, ItemsChanged,
FailureReason?`

What every background task has in common, and the only table the tasks page reads (D40). Written
by `BackgroundJobRunner` from the `JobRunOutcome` its job returned, so it cannot disagree with the
typed record the job also wrote. `Outcome` distinguishes success, nothing-to-do, failure and
cancellation — a cancelled run is not a failed one, and conflating them would raise a stalled
banner over a button somebody pressed on purpose.

`UnitKey` is the schedulable unit within a job, e.g. which source a sync read; null where the job
has only one. Due-ness is read from here rather than from `SyncRun`, which is what makes a
cancelled run skip its cycle instead of restarting on the next tick.

Ordered and paged by `Id`. A `DateTimeOffset` can be neither ordered nor compared in SQLite, which
is also why this is one table rather than a union over the typed ones.

Pruned to the last two hundred rows per task on insert. Runs that found nothing to do are kept —
a converged task and a broken one are otherwise indistinguishable — but a tick that was not due is
not a run and is not recorded.

### SyncRun

`Id, ProfileId, Source, StartedAt, FinishedAt?, Outcome, Created, Updated, Skipped,
ConflictsHeld, SlotsReleased, FailureReason?`

The audit trail for writes nobody watched (D21), and the source of "last synced 4 minutes ago"
and the pending-conflict badge. `Outcome` distinguishes success, nothing-to-do and failure —
a stalled sync must never render as "up to date".

A row is written only when a run has reached a terminal state, so `FinishedAt` is never null in
practice; it stays nullable because a run interrupted mid-flight is a state Phase 5c can reach and
this table should be able to describe. `StartedAt` times the work the row records rather than the
whole visit — for a reviewed sync that is the apply, since the gap between fetching and confirming
is a person thinking, not a sync running. Recency is read from the key, not this column: see §8.

### QueueItem

`Id, ProfileId, Position, AnimeId, AddedAt`. See D2, D15 and D23. A slot is always exactly one
title, so the unique index on `(ProfileId, AnimeId)` needs no filter and there is no check
constraint. Queueing a run of seasons appends them individually (D15, D24).

A slot's release depends on its `LibraryEntry` existing, because `AdvanceAsync` treats a missing
entry as unknown rather than watched. Anything that deletes an entry must therefore delete its
slot in the same transaction, or the slot becomes unreleasable (D19).

### RecommendationRun / RecommendationRunItem

Run: `Id, ProfileId, CreatedAt, ProviderName, ModelIdentifier?, CompletedCount,
CandidateCount, ResultCount, WasApplied`
Item: `Id, RunId, AnimeId, Rank, PredictedScore, Confidence, Reason?` — one title per
placement, never a franchise (D16). Check constraints keep `Rank >= 1` and `Confidence`
within 0.0–1.0, because these values arrive from an external model.

### Profile / ProfileSettings

Single default local profile; no registration, no OAuth, no auth in MVP. All library data
carries `ProfileId` so multi-user — post-MVP per §10 — stays possible. Settings per D7 and D20.

---

## 5. Service boundaries

| Service | Project | Responsibility |
|---|---|---|
| `ILibraryService` | Infrastructure | CRUD, status transitions, progress, scoring, filter/page |
| `IQueueService` | Infrastructure | add/remove/reorder, normalise positions, transactional |
| `IRelationBackfill` | Infrastructure | fills the relation graph in, and reports how much of it is known |
| `IRelationService` | Infrastructure | a title's relations, tagged and ordered; the sequel walk (D24) |
| `IImportService` | Infrastructure | orchestrates the import pipeline |
| `IRecommendationService` | Infrastructure | Phase 7 — build the export, validate a response, apply it, keep run history |
| `IAnimeListParser` | **Core** (incl. impls) | `MyAnimeListXmlParser`, `AniListJsonParser` — pure, no database |
| `IAniListClient` | Infrastructure | HTTP, GraphQL, paging, rate limits. Produces streams the parser reads |
| `ISyncService` | Infrastructure | Orchestrates fetch → preview → apply per source; owns `SyncRun` |
| `IAiRecommendationProvider` | Core | `ManualJsonRecommendationProvider` (Phase 7) and a hosted-endpoint provider (Phase 8). The interface is what keeps the second additive |
| `IRuntimeCalculator` | **Core** | episode×duration maths, sums, formatting |
| `ITaskRegistry` | Web | **Phase 15** — per-unit task state, the trigger channel and the cancellation source. The only thing the tasks page talks to; it never reaches a job (D40) |
| `IIdMappingJob` | Infrastructure | **Phase 9b** — maps a title to TVDB and IMDb ids from D46's dataset, carrying the season with them; gated on titles with no mapping (D25) |
| `IArtworkService` | Infrastructure | **Phase 9a** — fetches and caches one image per title per kind under `/data`; gated on what is missing and healed by what is on disk (D47) |
| `CoverImageResolver` | **Core** | **Phase 9a**, promoted from post-MVP by D25 and again by D34, and cited as `ICoverImageResolver` before it was built. Turns an image row into what a page should render — a served URL, a colour block, or nothing — which is why it is pure and lives here. Static rather than an interface, for `SourceLinkBuilder`'s reason: one implementation, no seam, nothing to inject. The reason it was drawn at all, that art must be served by AniQueue rather than hotlinked, is measured in §10 |

Import splits at the point where a database is first needed:

```
IAniListClient    (Infrastructure)  HTTP        → response stream(s)
IAnimeListParser  (Core, pure)      bytes       → ParseResult (entries + problems)
IImportService    (Infrastructure)  ParseResult → ImportPreview → commit
```

**The seam is `ParseResult`, and Phase 5a moves it.** `PreviewAsync` currently takes a `Stream`
and parses internally, which a sync cannot use — it has already fetched, possibly across several
responses. So `PreviewAsync(ParseResult, …)` becomes the primitive and the stream overload
composes parse-then-preview on top of it. `ParseResult` merges trivially: concatenate entries,
concatenate problems, and treat the whole fetch as rejected if any part of it was. That is what
makes "the difference is the trigger, not the logic" true rather than aspirational — sync is a
different fetch into the same preview, commit and advancement.

A partial fetch must be rejected outright rather than reported as a partial success: a truncated
list is indistinguishable from mass deletion, which is precisely the hazard D19 guards.

*One thing the merge does beyond concatenating*, added in 5b: an entry claiming an identifier an
earlier part already claimed is dropped. Within one payload a repeated identifier is a real
contradiction and the preview surfaces it as a conflict; across payloads it is an artifact of how
the list was chunked, and asking the user to resolve several hundred of those would be the
pipeline blaming them for its own paging.

**Parsers are resolved by key.** They are registered as one unkeyed singleton today and injected
singly, so adding a second implementation would silently rebind the first and start feeding XML
to the wrong parser.

*The AniList parser is keyed like every other, and only keyed.* It briefly needed a second,
concrete registration so a sync could pass the title-language preference to an overload the
interface could not express; storing each title against its language removed the reason for both
(D22).

**A parsed entry carries a set of identifiers, not one.** `ParsedLibraryEntry` holds a single
`Source` + `SourceAnimeId` today, which is enough for a format that knows only itself. AniList
supplies its own id *and* `idMal` in the same record, and writing both is what makes D17's bridge
work whichever source the user starts with. The MyAnimeList parser emits one identifier and the
AniList parser two; nothing downstream needs to know which produced what.

**D9 — parsing lives in Core, and the parser does not build the preview.** The brief's
`IAnimeListProvider.ImportAsync` returns an `ImportPreview` directly. But deciding whether
an entry is new, an update or a conflict requires reading the existing library, so such a
provider would need database access — which would drag every format parser into
Infrastructure and out of reach of fast, fixture-free tests. Splitting at this seam keeps
all format-specific logic pure, and leaves matching in exactly one place however many
formats exist. Adding AniList later means writing one parser, not a second pipeline.

`ImportPreview` is a pure in-memory object. **Nothing touches the database until the user
explicitly confirms.** Imports are idempotent where reasonable.

---

## 6. Cross-cutting requirements

**Security.** Anti-forgery; server-side validation; upload size limits; reject oversized
imports; secure XML (`DtdProcessing.Prohibit`, `XmlResolver = null`); HTML-encode user
content; no user-supplied file paths; no command execution; **never execute or evaluate AI
content** — it is untrusted data; no stack traces in production; secrets only via
environment/configuration. Forwarded headers honoured **only when explicitly configured**.
Never assume requests originate from localhost.

**Logging.** Structured `ILogger`. Events: startup, migration, import started, preview
generated, import committed, recommendation exported, recommendation imported, queue
changed. Never log whole uploaded files, AI payloads, or secrets.

**To stdout, and nowhere else.** The container runtime captures it and every layer above Docker
reads it from there; a file written inside the container is invisible to all of them, needs a
package the shared framework does not supply, and would grow inside the image's filesystem. What a
user needs without reading a log at all — which task failed, and why, in plain words — is on the
task's own row (D40). Phase 11's README covers `docker logs` and the `max-size` / `max-file`
options an operator should set, because the default `json-file` driver does not rotate.

**Performance.** Must handle several thousand anime. Server-side filtering, pagination or
virtualisation, `AsNoTracking` for reads, async EF, real indexes. Never load the whole
library for an ordinary page. AI export may intentionally load the full relevant set.

**Accessibility.** Semantic HTML; real `<button>` elements; keyboard-operable controls;
meaningful labels; sensible focus; a non-drag alternative for every drag action; adequate
contrast in both themes.

**Privacy in AI export.** Export only what ranking needs. Never email addresses, passwords,
API keys, IP addresses, server information, or personal notes unless explicitly opted in.
The UI states plainly what is being sent.

**Data integrity on import.** Match on external identity first — every identifier the incoming
entry supplies, in source-precedence order (D17) — then title matching only cautiously and never
silently merging ambiguous matches. An import must not overwrite manual queue position, personal
notes, hidden flag, or recommendation history unless explicitly requested.
Where a title is known to more than one source, D18 decides which one's tracking data stands.

**Outbound HTTP.** Every **host** AniQueue reaches on its own initiative is a constant, held in
code and never composed from user input, so there is no request-forgery surface. Account names
travel as GraphQL variables rather than in a URL. *This used to say "every endpoint", which was
true until something had to fetch a URL published by somebody else — see the image fetch below.*
**The scoring endpoint is the single
exception** and is settable, because a self-hosted model has no address anybody but the operator
knows; D38 replaces the protection a constant gave with three guards on what may be entered, and
bounds what a failing endpoint is allowed to say back. Cap the response size as import caps
upload size — a hostile or malfunctioning endpoint is
the same problem as a hostile file — and size the cap generously: a measured 753-entry library is
424 KB, so a few thousand entries is a few megabytes and a tight cap would reject a legitimate
large library. Do not persist cookies; the endpoint sets a session cookie that serves no purpose
here.

**Fetching an image is the one place a path comes from data** (D47). A cover URL arrives inside an
AniList response, so it is neither a constant nor user input, and it is not made settable — the
host set stays a constant in code and only the path varies. The fetch follows no redirects,
requires an image content type, caps the response as an upload is capped, and times out; a URL
failing any of those is recorded as a permanent failure rather than retried. Phase 9b widens the
host set and nothing else about this.

---

## 7. Phase plan

Every phase ends **buildable, tested and green**. `dotnet build` + `dotnet test` at each
boundary. Phases are front-loaded so a genuinely useful application exists from Phase 4
onward even if later phases slip.

**7 through 11 were renumbered by D31**, which left 12 vacant rather than renumbering what
followed: these numbers are cited from code comments, commit messages and PR titles that already
exist, so a gap costs less than making older citations point at the wrong work. **Phase 12 now
holds optional single-user authentication**, which arrived without a number of its own and needed
to land before Phase 14 rather than after it. The gap is spent; nothing is renumbered.

**Phase 15 is the same situation and takes the same answer.** Background tasks (D40, D41, D42)
were not in the original plan and are the next thing built, ahead of Phase 9 — and `Phase 10a`,
D36's per-source move, runs before even that because D40 depends on it. Renumbering to put them
in execution order would mean rewriting 49 phase citations in this file and the phase numbers
carried in source comments, making every existing citation point at the wrong work. **The number
is identity; the table is not a running order.** The order of work is: 10a, 15a–15e, 16, then 9a
and 9b, then the remainder of 10, then 11 onwards.

**Phase 16 is small and jumps the queue for the same reason 15 did**: it corrects something the
application is doing wrong right now, every time the sweep runs, and every day it waits is another
day of scores that cannot be compared with each other (D43).

**Phase 17 does not jump the queue, and that is the point of writing it down.** It collects what
was found while establishing D45 — that two of three models tried could not answer at all — and
most of it cannot be settled without a wider sample of models to settle it against. It waits, and
it waits *written*, because the alternative is finding the same things twice. The remote route is
opt-in as of D45, so nothing in it is costing a user anything today.

**Phase 9 splits, and one half of it stops waiting** (D46, D47). The licence that blocked it has
been read: the dataset this file named has none at all, and an MIT-licensed one takes its place at
the cost of a TMDB column. That unblocks both halves — but only `9b` ever needed a dataset, and
`9a` is where §10's argument actually lives, so the split lets the visible half ship while 9b's
API terms are still unverified.

**So the table is in number order and the `Done` column carries the running order**, because those
are two different questions and one table answering both by sorting would have to give up the
numbering. What is finished is a column; what happens next is the first row without a tick.

| # | Phase | Done | Exit criteria |
|---|---|---|---|
| 0 | Foundation | ✅ | Solution + 5 projects build; F5 serves the app; repo hygiene in place |
| 1 | Domain + persistence | ✅ | Migration applies to a fresh DB; indexes exist; a fresh install starts empty |
| 2 | **Vertical slice** | ✅ | MAL XML → preview → confirm → SQLite → backlog list, end to end |
| 3 | Backlog page | ✅ | Search, filter, sort, page, bulk actions |
| 4 | Up Next | ✅ | Reorder correct and persistent; queue advances when status changes |
| 5a | Reconciliation groundwork | ✅ | External identity is a set; precedence honoured; MAL import unchanged and green |
| 5b | AniList read sync, on demand | ✅ | Sync Now lands the user's list; runtime and decade filters work for the first time |
| 5c | Unattended sync | ✅ | Queue advances with nobody present; stalled sync is visible |
| 6a | Retire franchises | ✅ | Entity, columns and surfaces deleted; migration applies; suite green |
| 6b | Relations + backfill | ✅ | Edges land from a paced pass that is idle in the steady state |
| 6c | Related titles | ✅ | Every row expands to its relations, tagged; standalone filter returns |
| 6d | Queue what follows | ✅ | One click queues a title and its unwatched sequels, in release order |
| 7a | Scoring contract | ✅ | Request built, response validated, ranking applied — all of it without a page |
| 7b | Scoring surface | ✅ | Export, prompt, paste or upload, preview, apply |
| 8a | Settings store | ✅ | Every setting has one home; the application writes `userconfig.json` and both existing pages read through it |
| 8b | Scoring courier | ✅ | A stubbed endpoint returns a ranking that becomes a preview — client, guards and extraction, with no page involved |
| 8c | Scoring surface | ✅ | Remote and Manual cards; a run started, waited on, cancelled and applied without anything being copied by hand |
| 8d | Scheduled sweep | ✅ | A backlog scores itself in batches with nobody present, and idles when nothing has been rated |
| 9a | Cover art | ✅ | Covers cached under `/data` by a job that idles, served immutably, and rendered on the backlog and Up Next |
| 9b | Id mapping + richer art | ▢ **next** | Titles mapped to TVDB and IMDb from an MIT-licensed dataset, and richer art layered over the covers 9a already shows |
| 10 | Settings page | ▢ | One page for preferences; operator configuration shown and not editable |
| 10a | Per-source settings to the file | ✅ | `SourceSyncSettings` deleted; every sync setting read from `userconfig.json` |
| 11 | Docker + README | ▢ | Migrations squashed to one baseline; compose up, health check, container recreated without data loss |
| 12 | Optional auth | ▢ | A single-user login can be turned on; off by default, and off is still a supported deployment |
| 13 | CI | ▢ | Build and tests on every push; image built on a tag, published only once Phase 14 has run |
| 14 | Security pass | ▢ | §6's high-risk surfaces reviewed against the finished application; release gate opens |
| 15a | Job contract | ✅ | Jobs take a trigger and return an outcome; the runner drives units and reschedules nothing |
| 15b | Job runs | ✅ | Every executed run is recorded, including one that threw; every task reads its cadence from it |
| 15c | Tasks page + cadence | ✅ | Every task seen, started, cancelled and switched off from one page; one cadence drives them all |
| 15d | Scoring demolition | ✅ | No outbound scoring request has anybody waiting on it |
| 15e | Sources reshape | ✅ | Sources is configuration, one review button and a file import |
| 16 | Scoring without a rank | ✅ | The model returns scores and no ordering; nothing asks for, stores or shows a rank |
| 17 | Improve remote model scoring | ▢ *held* | A sweep gets past a batch it cannot score, reports itself as one run, and cannot be defeated by a history that outgrew the context |

**✅ done · ▢ not started.** *next* is what the running order reaches first; *held* waits on
something outside the repository — for 17, a wider sample of models to characterise (D45).

### Phase 0 — Foundation
Repo hygiene (`.gitignore`, `.gitattributes`, `.editorconfig`), solution and five projects,
`Directory.Build.props`, `Directory.Packages.props`, project references wired, placeholder
test in each test project. `.gitattributes` matters: Windows development, Linux container.

### Phase 1 — Domain and persistence
Entities and enums in Core. `AniQueueDbContext` with one `IEntityTypeConfiguration` per
entity. Indexes per §4. Initial migration. `IDbContextFactory` registration (D3). WAL and
`busy_timeout` applied at startup. Migrate-on-boot with explicit, readable failure logging
and graceful startup failure if the database is unreachable. A development-only seeder shipped
here covering completed titles with varied scores, planning, watching, several seasons of one
series, a queue and a recommendation result. *Deleted by D27 — a first run now starts empty.*

### Phase 2 — Vertical slice (the brief's §45 deliverable)
MAL XML import end to end. Secure XML settings, `0000-00-00` → null, status mapping,
size limits, dedup on `Source + SourceAnimeId`, preview summarising new/updated/skipped/
conflicts/invalid and totals per status, then explicit commit. Minimal backlog list to
prove the data landed.

*Described as delivered.* Phase 5a moves dedup onto `AnimeExternalId` (D17); everything else
here — the preview-then-commit split, the field-preservation rules, the conflict handling — is
what Phase 5 reuses rather than replaces.

### Phase 3 — Backlog page
Server-side search, filtering, sorting, paging/virtualisation. Filters: status, media type,
decade, runtime, score, source. Quick filters (Under 2h,
Under 6h, Movie, OVA, TV, decades, High AI confidence, Not yet ranked) — **each rendered
only when the backing metadata exists**. Bulk selection, bulk queue-add and bulk hide.
Anime cards degrade cleanly instead of printing rows of "N/A".

*Amended by D26: the selection is gone.* Every row carries its own add-to-queue and hide
control, because a backlog is read one interesting row at a time and selection priced the
common case as though it were the rare one. Mass hiding is the one capability withdrawn, and
D26 records what would bring it back. The "show hidden" quick filter goes with it: hidden is a
view in the status picker now, listing only what was set aside, which is the list somebody
actually wants when they go looking for something to restore.

No priority filter, sort or bulk action: manual priority does not exist (D14).

Defaults to **Planning**, with the status filter able to widen it. The brief defines the
backlog as what the user intends to watch, and Watching has its own page (§26); listing
every status by default buries the couple of hundred titles that are actually a decision
behind several hundred that are not.

Also adds a **source link per row** — "View on MyAnimeList", and AniList once that source
exists. This costs nothing: external identifiers are already stored by the importer, so the URL
is pure formatting with no lookup, no configuration and no new dependency. It is
worth having early because a backlog of several hundred titles constantly raises "what *is*
this one?", and answering it should not mean leaving the page to search manually.

From Phase 5a a row can carry several identifiers, so it offers a link per source rather than
one (D17) — a title bridged from MyAnimeList onto AniList links to both.

It is also the first implementation of the link provider described in §10, so Plex and
Overseerr later become configuration rather than new machinery.

Bulk actions run through `BusyScope` and off the circuit thread from the start, for the
reason recorded against the import: SQLite's provider is synchronous, so a bulk write
awaited inline freezes the entire circuit rather than just the page. *Withdrawn from this page
by D26 along with the bulk actions themselves — one row's write is not a bulk write. The rule
still holds everywhere it still applies: import, sync, and 6d's sequel walk.*

### Phase 4 — Up Next
`QueueService`: add, remove, move to top/up/down/bottom, transactional reorder with
position normalisation. Buttons first, then SortableJS interop (D5, §9).

**One crash found in use, and it is the interop's shape rather than SortableJS's.** Emptying the
queue removes the last row, the tbody stops rendering, and the teardown runs — twice, because
the guard was a null check followed by an `await` and `OnAfterRenderAsync` renders again while
the interop call is in flight. Sortable's `destroy` nulls its own element reference and then
writes to it, so the second call threw, and an exception escaping `OnAfterRenderAsync` ends the
circuit. **The field is claimed before the first await now**, teardown failures are logged
rather than propagated — it is cleanup, and losing it costs listeners on a circuit that is
ending anyway — and the JS guards on the element as well. Worth recording because the same
shape appears wherever a render callback disposes something asynchronously, and because no test
in the suite could have caught it (§8).

A run of seasons is queued by **expansion** (D15) — appended individually rather than as one
slot. Every slot is one title, which is what lets the user put something between two seasons.
*Amended by D23 and D24:* what expansion takes is a title rather than a franchise id, and what
it walks is the relation graph. Phase 4 shipped the franchise form; Phase 6d replaces it.

Also **queue advancement** (D12): when an import or sync reports that a queued title is no
longer Planning, its slot is released and positions are normalised, so the next item becomes
next in line without anyone pressing anything. This lives here rather than in the import
because it is a queue invariant, and it works with file import immediately — Phase 5c only
changes how often it runs.

### Phase 5 — AniList read sync

Read only (D13): no write-back, no scheduled re-ranking. Reconciliation reuses the import
pipeline rather than duplicating it — preserving every locally curated field and advancing the
queue via the Phase 4 rule. The difference is the trigger, not the logic.

Split into three because it was not one reviewable change. The seams are chosen so that all of
the AniList-specific risk is retired in 5b, while a human is still confirming every commit; 5c
then adds unattendedness to a path already proven by hand. That is the argument D5 made for
shipping buttons before drag.

#### Phase 5a — Reconciliation groundwork

No network, no new user-facing feature. `AnimeExternalId` and its migration, backfilling every
existing row from `Source` + `SourceAnimeId` (D17). Multiple `SourceLink`s per row. The backlog's
source filter and facet re-pointed at "is on this source" rather than "was created by it".
`PreviewAsync(ParseResult, …)` extracted as the primitive; parsers resolved by key.
`SourceSyncSettings`, precedence, and `LastWrittenBySource` (D18).

Everything here is provable against the existing MyAnimeList import, so the exit criterion is
that Phase 2 and Phase 4's suites pass unchanged with identity restructured underneath them.

**This phase ships no visible value**, which cuts against the front-loading principle above. It
is accepted deliberately: it is small, it is low-risk, and it is what makes 5b and 5c reviewable
instead of one enormous change.

#### Phase 5b — AniList read sync, on demand

**First task is a single request** answering the three assumptions D13 flags — unauthenticated
public list access, `score(format:)`, and whether `MediaListCollection` pages. Everything below
is designed for the answers being yes, yes, and no; verify before relying on it.

`AniListClient` in Infrastructure; `AniListJsonParser` in Core, tested against a captured
response committed as a fixture, so no test touches the network (§8). A **Sources** page: account
status, last sync, Sync Now, and the D18–D22 settings. Conflicts default to held for review, and
the existing import preview *is* the review surface (D21).

The mapping, which is where the fiddly parts are:

| AniList | AniQueue |
|---|---|
| `CURRENT` / `REPEATING` | `Watching` — see D15 on why `REPEATING` is not a planned re-watch |
| `PLANNING` / `COMPLETED` / `DROPPED` | `Planning` / `Completed` / `Dropped` |
| `PAUSED` | `OnHold` |
| `format` `TV` / `TV_SHORT` | `Tv`. The query pins `type: ANIME`, so manga formats never arrive |
| `score(format: POINT_100)` | `score > 0 ? max(1, round(score / 10.0, AwayFromZero)) : null` |
| `startedAt` / `completedAt` FuzzyDate | `DateOnly?`; a partial date is null, as `0000-00-00` already is |
| `duration`, `seasonYear`, `coverImage.extraLarge` | `EpisodeDurationMinutes`, `ReleaseYear`, `CoverImageUrl` — the cover field becomes `coverImage.medium` landing on an `AnimeImage` row in 9a (D47) |

Scores need the most care, and the probe changed the answer here. AniList users pick one of five
scoring systems and a raw `score` returns *their* scale, so an unconverted read gives 87 for a
100-point user and violates `CK_LibraryEntries_UserScoreRange` mid-transaction. Asking the API to
convert is right; **asking it for `POINT_10` is not.**

`score(format: POINT_10)` returns an integer, because AniList rounds during conversion — measured
half-up, since a 10-point 5 becomes a 5-point 3. So a 100-point user's score of 4 converts to 0.4
and comes back as **0**, which is indistinguishable from unscored. The scale that is supposed to
protect low scores is the one that destroys them.

Requesting `POINT_100` instead — the finest-grained integer scale, which every native format maps
onto without loss — keeps 0 meaning exactly one thing, and leaves the 1–10 mapping ours to do:
divide by ten, round away from zero, and clamp up to 1 so a 4/100 becomes a 1 rather than
vanishing. Rounding happens *after* excluding zero. Away-from-zero is specified deliberately
because .NET's default `Math.Round` is banker's rounding, which would send 8.5 down to 8.

A 1 is useful signal — it separates a disliked show from an unrated one, which is exactly what
Phase 7 ranks on. In the measured library 188 of 753 entries are unscored, so the zero branch is a
quarter of the data rather than an edge case.

Coarse native formats stay coarse: a 3-smiley user's history compresses to three distinct values
whatever we request, so Phase 7's ranking should not claim confidence it does not have.

**Runtime and decade features start working here**, and this is the phase's most visible effect
besides the sync itself. `EpisodeDurationMinutes` and `ReleaseYear` have never been populated
outside the development seeder, so Phase 3's runtime filter, runtime sort and *Under 2h* /
*Under 6h* / decade chips have been inert in every real installation, and Phase 7's *Something
short* and *One evening* modes were blocked on data nothing supplied.

The measured coverage is better than expected: **not one of 753 entries had a null `duration`**,
and only 13 lacked `seasonYear`. So for AniList-known titles these features are effectively
complete rather than partial. They remain empty for MyAnimeList-only and manual rows, so the
surfaces must still say what they are filtering over — `RuntimeCalculator.Sum` already reports
`IsPartial` for exactly this reason.

**Amendments made while building it.** Recorded here rather than left as drift between this
section and the code:

- **`SyncRun` moved here from 5c.** This phase has to render "last synced", and nothing else can
  say it. An on-demand run also deserves the same record an unattended one gets, so 5c adds the
  loop rather than the table. A row is written when a run reaches a *terminal* state — a failure,
  or a list that already matched — and a preview waiting on a person is not terminal. Recording
  one would let the page report the library as up to date while the changes sat unconfirmed on
  screen. The kill switch writes nothing at all: nothing was attempted, and a log of runs that
  never ran buries the failures that did.
- **Catalogue fields follow one rule: a value replaces, a null leaves alone.** Otherwise the
  consolidating user's next MyAnimeList import blanks the duration, year and art an AniList sync
  had just supplied, for every title the two lists share — turning Phase 3's filters back off.
  D18's precedence guards tracking data; nullness guards catalogue data.
- **A cover URL that merely moved is not a change.** It is nearly always the same picture behind
  a rotated CDN path, and reporting it would turn an idle sync into a library-wide review list —
  the churn D21 assumes away when it says a sync that changes nothing writes nothing. Gaining art
  where there was none is reported.
- **The parser has no opinion about title language.** It briefly took the preference through an
  overload `IAnimeListParser` could not express, which needed a second DI registration to reach.
  Storing each title against its language removed the reason for both: the parser carries every
  variant the source published, and the import resolves which to display (D22).
- **Chunk following belongs to the client, with a hard ceiling of 20 requests.** `hasNextChunk`
  is the other end's word, so an unbounded loop is a request loop with no exit. Hitting the
  ceiling **fails the fetch** rather than keeping what arrived, and `ParseResult.Merge` enforces
  the same rule when parts are joined — half a list is precisely what D19's absence handling
  would read as a mass deletion.
- **No `AddHttpClient`.** It would mean a package reference to manage a single long-lived client
  to a single host, which §12 requires approval for. The two things the factory would supply are
  done explicitly: a pooled connection lifetime so a long-running container notices DNS changes,
  and one shared instance rather than a socket per call. Cookies are off, because the endpoint
  sets a `laravel_session` nothing here wants.
- **Settings that do not act yet are shown, grouped and labelled.** Unattended application,
  conflict policy and absence policy only mean something once 5c runs on a timer. They sit under
  a heading saying AniQueue does not sync on a schedule yet, because a control that silently does
  nothing reads as broken — and omitting it reads worse to someone who has just read what
  unattended sync will do.

#### Phase 5c — Unattended sync

A `BackgroundService` on a `PeriodicTimer`, a scope per tick, and **ticks that never overlap** —
a slow response must skip the next tick, not queue it, or one timeout turns a five-minute interval
into concurrent syncs racing each other. Unattended application and the absence policy (D19, D21).
Staleness notification, failure surfacing and backoff.

*`SyncRun` and the configuration kill-switch landed in 5b* — the Sources page needed both — so
this phase writes rows to an existing table rather than creating one. The poll-interval floor is
still operator configuration and still arrives here, since nothing polls before it.

**Write it as a job runner that happens to have one job.** AniQueue ends up with several timed
background tasks — metadata and artwork enrichment, and eventually scheduled re-ranking — and the
loop each of them needs is identical: tick, open a scope, refuse to overlap, catch, record, back
off. Expressing that as "run this job" rather than "run the sync" costs about twenty lines either
way and makes the second job additive instead of a refactor. Nothing more than the loop is
generalised.

**Specifically, do not generalise the run record.** `SyncRun`'s columns — created, updated,
conflicts held, slots released — mean something for a sync and nothing for an artwork fetch.
Folding future jobs into one table forces either a JSON blob or a wide row of nullable columns
each belonging to one job type, which is the stringly-typed bag D7 rejected. A second job gets a
second typed table.

**And no background task page yet.** What such a page offers is observability and manual control,
and both already exist per job: the Sources page shows last success, last failure and Sync Now.
A combined view earns its place when per-job surfaces become worse than one shared surface, which
is around three jobs. Today there is one, and two of the three candidates cannot exist in the MVP
— metadata and artwork have no MVP consumer, and scheduled re-ranking has nothing to call, since
D11's recommendation workflow is a manual export and paste. The trigger for building it is a
second real job, not a date.

**This is the app's first background writer**, and §9's `SQLITE_BUSY` risk stops being
hypothetical when it runs on a timer. WAL and `busy_timeout` are already configured and are the
right tool — a 30-second budget against millisecond writes, failing as a retry rather than
corruption — so no application-level lock is added. What is required is non-overlapping ticks
and a commit that is skipped entirely when nothing would change.

**Pages must not change under the user**, which is already the default: Blazor Server re-renders
only when something calls `StateHasChanged`, and a background write in its own scope cannot
reach an open circuit. So the work is the opposite — telling an open page it went stale, via a
singleton event pages subscribe to and *reliably unsubscribe from*, marshalled onto the render
thread as `BusyScope` already does. The page then offers a refresh button rather than moving
under the cursor. Sync Now is exempt: the user asked, so it refreshes.

Staleness here is safe rather than merely tolerable, and Phase 4 made it so on purpose. Every
queue mutation resolves against the database inside its transaction rather than against the
rendered page, and `MoveAsync`/`RemoveAsync` key on `QueueItemId`, so acting on a stale Up Next
either does the right thing or returns false and logs.

Failure must be legible and must never look like success. A GraphQL `errors` array arrives with
HTTP 200 and is a failure, not an empty list; a private or mistyped account resolves to nothing
and is a configuration error, not a successful sync of zero entries — under an absence policy of
remove, that distinction is the difference between a warning and an emptied library. The Sources
page shows last success and last failure separately, because "last synced 3 hours ago, last
attempt failed: profile is private" is actionable where "sync failed" is not, and sustained
failure escalates to a banner on Up Next, whose correctness silently depends on sync running.

**What 5c decided that this section did not.** Four things had to be settled while building it,
and each is load-bearing enough to record rather than leave in the code.

- **The schedule is a fixed set, not a number of minutes** — off, hourly, six-hourly, daily,
  weekly — stored per source as `SourceSyncSettings.Schedule`. A free-form interval invites
  "every two minutes" from a user with no way to know the measured rate limit is 30 requests a
  minute, and every value here is a promise about load on somebody else's service. *This
  retires the poll-interval floor* that D20 listed as operator configuration: the floor existed
  to bound an arbitrary number, and the shortest value offered is now an hour. Adding an inert
  configuration key ahead of the behaviour that needs it is what D11 argues against, so it was
  not added. A cron-style schedule is out of the MVP; the floor comes back with it.
- **It ships switched off.** An installation upgrading with an account already configured does
  not start fetching on its own — turning it on is the act that carries the intent. That is why
  the phase's exit criterion is provable rather than automatic: the queue advances with nobody
  present *once a schedule is set*.
- **Absence is recorded on `AnimeExternalId`**, as a nullable `MissingFromSourceAt`. Absence is
  a fact about a title on one service, which is exactly what that row already is — a title
  dropped from AniList while still on MyAnimeList is absent from one and present on the other,
  and a flag on the library entry could not say that. It is written during the fetch rather than
  the commit, because the case absence exists for is a list that is otherwise identical, and
  such a fetch has nothing to apply. It is cleared the moment the source lists the title again,
  so it always describes the latest fetch rather than accumulating history — which also makes it
  the exact population an automatic `Remove` would act on, whenever D19's guards land.
- **`SyncOutcome` gains a fourth value, `HeldForReview`, and `SyncRun` a `ChangesHeld` count.**
  With unattended application switched off, a run that finds twelve changes and applies none had
  no honest row to write: `NothingToDo` would tell the user their library matches their list,
  and `Failed` would put a red banner on a setting working exactly as configured. Only the count
  is stored, per D21 — the changes themselves are recomputed by the visit that reviews them.

**One bug fell out of building it**, in the import path rather than the sync. D21 asserts that a
genuinely ambiguous match "produces a conflict with no candidate id, which existing code already
downgrades to skip". That was true of the no-identifier path and *not* of the hand-added-twin
path, which took the first same-titled row by query order and offered it as the candidate. Under
`LinkToExisting` an unattended run would have merged into an arbitrary one of two identical rows.
Two or more unidentified twins now produce a conflict carrying no candidate, which is both the
honest preview for a person and the thing that keeps the automated resolution unreachable.

**A settings reset belongs to Phase 10, and is recorded here because 5c is where the need
appeared.** D20's split means a clean slate takes two actions: renaming `userconfig.json` clears
the operator's half, and the user's half lives in the database. Moving preferences into the file
would fix that at the cost of a config writer D20 declined — atomic replacement, concurrent-write
protection and comment-preserving round-tripping — and would put settings outside the backup
the database file already is (D33). The cheaper answer is a **"reset settings to defaults"**
action that deletes the profile's `SourceSyncSettings` and `ProfileSettings` rows and leaves
the library alone: no writer, no precedence puzzle, and it resets preferences a *user* set,
which renaming an operator file never would. Three things make waiting safe — every
user-preference default is the safe one (absence `Flag`, conflicts `HoldForReview`, schedule
`Off`), so "no row" and "clean slate" are the same state; and the recovery case that actually
matters is already covered completely by the kill switch.

### Phase 6 — Relations

*Formerly "Franchises". D23 deleted the entity and D24 settled what replaces it, so this phase
now builds the thing franchises were an attempt at: a title's relatives, shown against the
title, with no grouping anywhere.*

Split into four parts, because they fail for different reasons — deletion is mechanical, the
backfill is the only part that can be defeated by something outside the repository, and the two
surfaces on top of it are ordinary UI work.

**6a — Retire franchises.** `Franchise`, the three `Anime` columns, `ShowOptionalFranchiseEntries`,
`FranchiseFilter`, `LibraryFacets.HasFranchises`, `AddFranchiseAsync`, `QueueableFranchise` and
both `FranchiseName` projections, with the migration that drops them. The queue is untouched by
design: since D15 a slot has referenced a title directly, which is why this is a straight drop
rather than the data migration `FranchisesAreNotQueueItems` had to be. D23, D24 and D25 land in
the same PR, per §12.

**6b — Relations and the backfill.** `AnimeRelation` stores edges as **external ids** —
`(Source, ExternalId, RelationType, RelatedExternalId)` — rather than as `AnimeId` pairs, unique
on all four, with a second index on `(Source, RelatedExternalId)` because both ends are queried.
Relations routinely point at titles the user does not own, and resolving at write time would
discard those. Edges are stored **exactly as fetched** and inverted on read: AniList states an
edge from the perspective of the media queried, so normalising at write time would lose which
end spoke.

The fetch is a separate query rather than a field on the list query, and *should* be: relations
are near-static while a list changes constantly, so inlining them would refetch an immutable
graph on every poll. A batched pass — `media(id_in: [...])`, 50 per request — costs roughly
fifteen requests for a 750-title library once, and zero in the steady state. It also carries
`startDate` (release ordering needs a date finer than `ReleaseYear`, since split-cour seasons
share a year) and `coverImage.color` (six bytes, 92% coverage, and the query is already being
edited — D25).

**Pace it.** The measured rate limit is 30 requests a minute, not the documented 90, so a
fifteen-request backfill is half a minute's budget in one burst. Honour `X-RateLimit-Remaining`
and `Retry-After` and spread it, rather than discovering the limit through 429s. Tested against
`TimeProvider` rather than by sleeping, the same way 5c tests scheduling.

It runs as a second `IBackgroundJob`, which is what that interface was written for. Work is
"any `AnimeExternalId` with a null `RelationsFetchedAt`" — a marker meaning **we asked**, not
*we got edges*, or a title with no relations is refetched forever. It **respects the
`Sync:Enabled` kill switch**, being unattended outbound traffic, and it **degrades silently**
(D25): a count on the Sources page, no `SyncRun` rows, no banner.

**A new season needs no re-fetch**, and that is worth stating because it is the case everyone
assumes is the problem: a new season arrives as a *new* title with no marker, gets fetched, and
its own edges point back at the older seasons — which the reverse index already finds.

**What does need re-reading is both ends already owned:** a relation added or corrected between
two titles the library already holds. Editors reclassify a side story as a spin-off, add a recap
film's link, or fix an edge that was wrong. So an answer **expires after thirty days** and the
title re-enters the same lazy pass, oldest-unanswered first.

Thirty days, and **fixed rather than configurable**. The graph changes on the timescale of
production announcements, and "how often should relation metadata be re-read" is a question
nobody has an opinion about or evidence for — a setting would be a control nobody touches and a
migration to carry it. A **Refresh related titles** button on the Sources page covers impatience,
and is the only user-triggered path into any of this.

**Re-reading forces reconciliation, and that is not optional.** The first pass could only ever
add, so a refresh that also only added would *confirm* a withdrawn edge rather than remove it —
achieving less than half of what re-reading is for. Edges the source no longer publishes are
deleted, scoped exactly as D19 scopes absence: **only for titles the response actually spoke
about.** A title a batch did not mention keeps everything it had, because a gap is not a
statement.

Deliberately **not** narrowed to titles still airing, which would cut the population by most of
it: the interesting case is a finished show from 2005 gaining a sequel announced in 2026, and
status-based targeting misses exactly that.

**Scope is every owned title carrying an AniList id**, whatever its status: a Completed prequel
has to be displayable as a relative. A MyAnimeList-only library reaches none of this until
D25's id-mapping job ships — stated in D23 as a real gap.

**Measured against the live API on 2026-08-19**, with a real 754-title library rather than
assumed:

| Assumption | Result |
|---|---|
| Fifty ids in one request | **Yes.** 49 of 50 media returned in 34 KB; the missing one does not exist, which is the case the marker has to survive |
| The rate limit is 30, not the documented 90 | **Confirmed again.** `X-RateLimit-Limit: 30`, and `X-RateLimit-Remaining` is present on every response |
| The declined relation types are not rare | **They are most of the noise.** Of 438 edges in one batch, 79 were `CHARACTER` or `OTHER` and 46 were `ADAPTATION` |
| The node-type filter is load-bearing | **Yes.** 81 of 438 nodes were `MANGA`, which no other guard would have caught |
| A whole library converges in one visit | **Yes.** 754 titles asked about, **1,275 edges stored**, and every later tick did nothing at all |

**6c — Related titles in the backlog.** Every title keeps its own row; each row expands to its
relatives, owned only, tagged with the relation type. One edge out from the title, never
transitive — season 5 is not the sequel of season 1, and a walk that kept going would pull a
whole franchise into a panel opened to answer a much smaller question. Ordered by release date,
unknown dates last. Hidden entries are excluded; every other status is included, because an
expansion is context rather than results.

The page gains **one** query: a grouped count of displayable relatives for the fifty visible
rows, so a row with no relatives shows **no chevron at all**. A control that sometimes does
nothing teaches people to stop pressing it, which would kill the discoverability the phase
exists for. Detail loads on expand.

The **standalone filter** returns here in D24's redefined form — no `PREQUEL` or `SEQUEL` edge,
counted over all edges rather than only owned ones — as an indexed `EXISTS`, the same shape as
the filters already in `LibraryService`. It is offered only once the graph holds a prequel or
sequel edge: before the backfill has run, and forever for a MyAnimeList-only library, it would
match every row, and a filter that changes nothing reads as *everything I own is standalone*.

**"Related" was written here as the label for a transitive connection, and it cannot be that.**
Nothing is transitive, by the sentence immediately after it, so that label could never appear.
What actually earns it is narrower and real: **the two ends disagree.** AniList publishes
`PARENT` as the counterpart of both `SIDE_STORY` and `SPIN_OFF`, so one pair routinely arrives
as a spin-off read from one side and a side story read from the other. Naming a winner would
state a relationship the source did not, and naming the first one seen would make the label
depend on row order — so a disagreement is labelled "Related" and nothing else is.

**The development seeder was given a graph here**, so the expansion could be looked at without
syncing a real account first, with invented AniList identifiers deliberately outside the range
AniList issues. *That is what D27 later deleted, and this is the paragraph that caused it:*
invented identifiers are indistinguishable from real ones that a source has stopped listing, so
the first sync against a real account reported five sample titles as missing from it.

**One Razor trap, found the way they all are.** A `@* … *@` comment placed *between attributes*
inside an element's opening tag compiles without complaint and then fails at render, as
`setAttribute` rejecting the comment text as an attribute name — a runtime failure that takes
the circuit down, from markup the compiler accepted. Comments go above the element. Nothing in
the suite could have caught it: there are no component tests, which is the actual gap.

**6d — Queue what follows.** `AddWithSequelsAsync(profileId, animeId)` walks `SEQUEL` forward
from one title and appends what is still Planning, in release order, skipping `SUMMARY` and
`COMPILATION`. It traverses **through** titles it will not queue — a Completed season four
between three and five must not stop the walk — and it writes nothing itself, handing the
ordered set to `AddAnimeAsync` so the contiguity invariant keeps one home. Individual adds from
an expansion are the other half, and the two together replace what `AddFranchiseAsync` did.

**The individual half shipped first, and took the checkboxes with it (D26).** Every backlog row
and every relative in an expansion carries a **+** that queues that one title, disabled where it
could not work and saying why. The walk is the other half, and it is the only part of 6d a
per-row button cannot express — and the only part that still needs `QueueAddResult`'s per-reason
counts, since a run of six can decline several of them for different reasons.

**The walk lives in the expansion panel, not on the row.** A control that queues six things at
once belongs beside the list of what they are, and the row already carries two buttons that D26
had to move apart. It is the only worded button on the page, because "queue six titles" is not
something a glyph should be trusted to say, and it carries the count so the size of the
commitment is stated before it is made rather than after.

It is offered only when it would do more than the row's own button already does — at one title
it *is* that button with a longer name — so a run already queued shows nothing, and the number
is recomputed after every press on the page, in either direction.

**Two things about the walk that the sentence above got wrong, and one it left ambiguous.**

- **`CountSequelsToQueueAsync` had to exist**, which nothing planned for. A button that says
  what it will do has to ask first, and it has to ask the same question the press answers, or
  the count and the action disagree the moment anything else is queued.
- **The walk goes through titles the library does not own**, not merely through titles it will
  not queue. It runs in external identifiers and resolves to library rows only at the end,
  because an unowned middle season has edges and no `Anime` row — and stopping there would end
  the chain at exactly the gap the feature exists to bridge.
- ***"Skipping `SUMMARY` and `COMPILATION`" is about nodes, not edges,*** which is the only
  reading that is not vacuous: the walk follows `SEQUEL` alone, so it never traverses either
  type. What it means is that a title *identified as* a recap or a compilation is passed through
  rather than queued — and that case is routine rather than theoretical, because AniList threads
  a recap film as the sequel of one season and the prequel of the next, putting it in the middle
  of the chain rather than off to one side.

  **One direction of that cannot be read**, and it is left wrong on purpose. `COMPILATION` has
  an inverse in AniList's vocabulary and `SUMMARY` does not — `RelationTypes.Invert` maps it to
  itself — so only an edge stating "X has summary Y" identifies Y as the recap. A recap whose
  own fetch stated the edge from its side is indistinguishable from the series it recaps.
  Excluding both ends would drop the season instead, and queueing a recap the user removes in
  one press is much the smaller error.

**A cycle terminates**, because relation data is maintained by people and a graph saying two
titles follow each other is a mistake to survive rather than spin on. The visited set does that;
a step limit bounds length as well, since an unbounded transitive walk over data an external
editor reshapes is a page that hangs rather than a page that is wrong.

### Phase 7 — Scoring interchange
Exports the backlog as versioned JSON for an external model to rank, and imports the ranking
back. Both halves are schema, and the schema is the deliverable: the export states what a model
is given, the response schema states exactly what AniQueue will accept back, and nothing
outside it is parsed.

**Split in two while building it.** *7a* is everything below except the page: the payload types,
the prompt, the response parser and `IRecommendationService`, ending with a request that can be
built and a ranking that can be applied by a test but by nobody else. *7b* is the surface that
lets a person do it. The seam is worth having beyond review size — 7a is what Phase 8 calls, so
building it alone first is what proves the endpoint is a second courier rather than a second
pipeline (D31).

**The prompt belongs to 7a, not to the page.** What a model is told to return and what the
parser accepts are two statements of one thing; putting the first in a Razor component would
leave Phase 8 reaching into the UI for it, and would let the two drift with nothing failing.
A test asserts that the example the prompt asks for is one the parser accepts.

**The export payload.** One candidate per Planning title: the AniQueue anime id, the displayed
title and whichever romaji/english/native variants the source published, media type, episode
count, episode duration, release year, and every external identifier the title carries (D17) so
a model can recognise a show it knows under another service's name. Alongside the candidates,
the *history* that makes a ranking personal — completed titles with the user's own score.
Without it a model ranks by general reputation, which is exactly the prioritisation this
application exists to avoid.

**`PersonalNotes` is excluded unless opted in.** `ProfileSettings.IncludePersonalNotesInAiExport`
already exists and already defaults to false. §6's rule is unchanged: export only what ranking
needs, never credentials, never an email address.

**The response schema.** An array of results, each naming a candidate by its AniQueue anime id
and carrying rank, predicted score, confidence 0–1 and a short reason — the four columns
`RecommendationRunItem` already has. Validation is strict and total: unknown ids, duplicate
ids, rank collisions, out-of-range scores or confidences, candidates never sent, and candidates
sent that did not come back are each reported. **A response that fails validation is not
applied in part**, because a half-applied ranking is indistinguishable afterwards from a
complete one.

**Applying writes a `RecommendationRun` and denormalises onto the entry** (D4). Those tables and
columns exist and have never been written to. It touches `RecommendationScore`,
`RecommendationConfidence`, `RecommendationReason` and `RecommendationUpdatedAt`, and nothing
else — never status, never progress, never the user's own score, and never
`QueueItem.Position`. The model proposes an order and the user owns one; D11 is the reason they
are separate columns rather than one contested column.

**The manual path is the path, and it stays.** Download or copy the request, paste the reply
back, preview, apply. Phase 8 automates the carrying and does not replace this: a hosted model
that is switched off is the normal state of a self-hosted install, and this is also the
fallback whenever a configured endpoint returns something the schema rejects.

**How much to send is the user's, not ours.** Both bounds — how many titles to offer for
ranking, and how many scored titles to carry as history — are `ProfileSettings` columns edited
on the Recommendations page itself. They are properties of somebody else's model, which
AniQueue cannot see; the measured library builds a 105 KB request uncapped and 5.3 KB at five
candidates, and only the person running the model knows which of those it can read. The page
states the size for that reason. Phase 10 offers the same two values beside the other
preferences rather than inventing its own.

**The two directions are bounded separately**, because their costs are not alike. A long request
is read once; a long *reply* is generated a token at a time and can exhaust a model's output
budget halfway down the list. So "rank the best 50 of these 182" is a third setting, and it is
not the same request as sending 50 titles — every candidate is still weighed against the
history, and only the top of the result comes back. The prompt states it in that order, and
says so twice: a model told only "return 50" ranks the first fifty it reads, which is a worse
answer that looks identical in the reply.

**A cap is a page size, not a horizon**, and that is the whole design of the option. A capped
request takes the titles longest without a score, never-scored first, so running it repeatedly
sweeps the backlog and then keeps it fresh — where taking the first fifty alphabetically would
leave the second half of a library unranked however many times it ran. It follows that the
preview must check a reply against *what was asked* rather than against the backlog: with a cap
set those differ, and reporting the difference as missing would turn the user's own setting into
a warning against itself. A ranked title that was never offered is skipped with that reason on
its row.

**SignalR's default message size is smaller than this application's largest input**, and that
is a property of Blazor Server rather than of this page. A ranking of a real backlog is tens of
kilobytes; pasting one into a bound control sends the whole value in a single hub message, and
past the 32 KB default the *circuit is closed* rather than the value rejected. The symptom is a
page that quietly stops responding — the paste appears to do nothing, the button stays
disabled, and no server-side code ever runs, so nothing can say why. `MaximumReceiveMessageSize`
is therefore raised to match what the parser accepts, which moves the refusal to code that can
explain itself. Any future surface taking a large pasted payload inherits this.

**The clipboard is not available where this application runs**, which 7b found and which is
worth stating because it is a property of the deployment rather than of one page.
`navigator.clipboard` exists only in a secure context, and a self-hosted AniQueue is reached
over plain http at a LAN address far more often than over https — so on the target deployment
the modern API is simply absent. Copy therefore falls back to `execCommand`, and **both the
request and the instructions are rendered on the page** rather than only offered through a
button: text a person can select by hand is the one route that always works, and the copy
buttons are a convenience over it rather than the way in. Anything later that offers to put
something on the clipboard inherits the same constraint.

*Also from 7b:* the request is built once and held, so the file, the clipboard and the text on
screen are three views of one string. Rebuilding it per action would let a sync land between
two of them and hand the model a payload that does not match what the user is reading.

**Full-library backup and restore are declined here** (D33), and MVP criteria 23–24 with them.
This phase exports what a ranking needs, which is a different payload from a restore — queue
order, hidden flags, settings and run history are all deliberately absent from it.

### Phase 8 — Hosted model scoring
The same round trip as Phase 7 with the copying removed: AniQueue posts the generated prompt
and candidate payload to a model the operator hosts — LM Studio, Ollama, anything speaking a
chat-completions API — and parses the reply against the Phase 7 response schema. What is sent
and what is accepted do not change. Only who carries it does, and then how often.

**Split in four**, because it grew three things after it was first written: a settings store
that is not scoring work, an unattended sweep that D31 did not anticipate, and a page that is
now a restructure rather than a button. *8a* is the settings store. *8b* is the courier, ending
with a stubbed endpoint producing a preview and no page involved — the review where the guards
are either read or not. *8c* is the surface. *8d* is the sweep.

**No API key is required for any of this**, and Phase 11's README promise survives literally: a
self-hosted model is reachable without credentials, and Phase 7's manual path remains permanent
for anyone who would rather not host one at all.

#### Phase 8a — Settings store
D36's file, and both existing pages moved onto it. The application regenerates
`userconfig.json` whole on every save from the key set it knows, writes through a temporary file
and a rename, and reports an unwritable directory rather than failing over it — the behaviour
the inert template already has (D20).

**One migration, and it only removes.** `RecommendationHistorySize`,
`RecommendationCandidateLimit`, `RecommendationReturnTop` and `IncludePersonalNotesInAiExport`
leave `ProfileSettings` for the file; `RecommendationRun` gains a duration, so the second run
onwards can say how long the first one took. Phase 11 squashes the history immediately
afterwards, so a column added here costs nothing.

`IRecommendationService` loses `GetOptionsAsync` and `SaveOptionsAsync`. It already takes
`ScoringRequestOptions` as an argument; this removes the only place it also owned them.

**The AniList account becomes editable on the Sources card**, which is a field once the store
exists and is what proves the store with two consumers rather than one. It is the same value it
always was; what changes is that a page may write it (D36).

#### Phase 8b — Scoring courier
A `POST` to `{endpoint}/v1/chat/completions` carrying the prompt as the system message and the
payload as the user message — byte-identical to what the Manual card puts on screen, so the two
routes carry one contract and cannot drift (D31). Its own `HttpClient`: the AniList one's
thirty-second timeout is right for a list and absurd for a model, and the two should not share a
ceiling.

**`max_tokens` is calculated, because it is the failure everybody hits.** A reply is generated a
token at a time and most local servers cap output far below what two hundred rankings need; the
model then stops mid-object and the reply is malformed JSON for a reason the user did not cause.
`ExpectedResults` is already on the request, so the ceiling is derived from it — and where the
server reports `finish_reason`, truncation is read off the response rather than inferred. What
it reports is the lever that fixes it: ask for fewer rankings, or raise the server's limit.

**Guards per D38, extraction per D37**, both tested here rather than through a page.
`temperature` is fixed low and is not a setting: it is a correctness knob, and the same backlog
should rank roughly the same way twice. Nothing streams — a partial ranking cannot be validated
or shown, so the complexity buys nothing.

**Failure reports, unlike enrichment.** D25 has enrichment degrade silently because a missing
detail is not a wrong library. A scoring run has somebody waiting on it, so it says which side
failed: nothing answered, the model ran out of room, the schema rejected what came back — and
for the last, what came back, bounded per D38.

#### Phase 8c — Scoring surface

> **Partly withdrawn by D42.** The card shape, the disclosures grouped by what they govern, the
> shared sizes and the test all stand. What is deleted is the run started from the page and
> everything built to make waiting on it bearable — so "the wait is honest" below describes a wait
> that no longer exists, and is kept because the reasoning about `OperationProgress` and about what
> a cancel costs is what D42 weighed against.

The page becomes a Sources page (D35): a Remote card, a Manual card, a shared *How much to send*
card, and a preview that replaces them. Settings sit behind `<details>` disclosures grouped by
what they govern rather than by which card they sit on — connection, schedule, and the shared
sizes — because the sizes govern a run down either route and drawing them twice is D30's bug.

*Phase 7b's note that a disclosure "does not read as settings you can change" is corrected here
rather than left contradicting the code:* it was learned about a panel floating above a request
summary on a page with no card structure, and that page no longer exists.

**A run started by hand is one request**, bounded by the sizes the user set. Where it would ask
for more than a model can plausibly return, the card says so before sending and offers the two
real routes — rank fewer, or run the whole backlog on a schedule — rather than silently changing
what was asked.

**The wait is honest.** `OperationProgress` already covers this and already returns no fraction
when work cannot be counted, which is the truth here: nothing arrives until everything does. So
elapsed time, no progress bar, the previous run's duration for scale, and a cancel — which
`BusyDialog` gains as an optional callback, since it is modal and nothing behind it can be
reached. Leaving the page abandons the run, and the card says so.

**Test sends the smallest request that exercises the whole path** — a real completion asking for
a two-line ranking of two invented candidates, through the same client and the same
`response_format` — and reports verbatim what came back. An endpoint that answers but cannot
produce JSON is a distinct outcome from one that does not answer, and it is the failure that
would otherwise surface only after a ten-minute run.

#### Phase 8d — Scheduled sweep

> **Amended by D40, D41, D42 and D43.** Its `Schedule` becomes the single task cadence; the gate
> and the interactive stand-down are deleted with the run they yielded to; a library change now
> wakes it for never-scored titles only. The batching, the error budget and the halving on
> `TooLarge` are untouched. **"Chunking does not distort the result" is wrong**, and D43 has the
> replies that show it.

`ScoringSweepJob`, the third `IBackgroundJob` and the one that interface named in advance. Its
`TickPeriod` is polling resolution, not schedule; the job decides whether it is due from a
setting that can change while the application runs.

**Every title sent comes back.** The return limit is a Manual lever and must not apply here: send
fifty, take the best twenty, and the other thirty stay unscored, are picked again next tick as
the never-scored ones, and the tail of the backlog is never reached. That is precisely the blind
spot "a cap is a page size, not a horizon" was written against, arriving by the other door.

**A sweep runs many batches, bounded by time rather than by count.** One batch per tick would
mean twenty-five titles a day on a daily schedule, so a due sweep runs batches back to back until
there is no stale work, a time budget expires, or its error budget is spent. A failed batch is
recorded and skipped rather than ending the sweep — one odd title must not block everything
behind it — and three consecutive failures stop it and leave the runner's existing backoff to
retry.

**Chunking does not distort the result, and the schema is why.** `rank` is placement within a
batch and never leaves it; `predictedScore` is a prediction against the user's history, which
every batch carries identically. `LibraryEntry` stores only the score, and the backlog sorts by
it — so the batch-relative number is never what is compared across the library. The remaining
effect is calibration drift between batches, which is a difference of degree from re-running one
batch twice rather than a difference of kind.

*That paragraph is left standing because the argument it makes is the one that failed, and the way
it failed is worth reading.* Every clause in it is true and the conclusion does not follow. Rank
does stay inside its batch — but a model asked for both a rank and a score will sometimes produce
the score **from** the rank, and the score is what leaves. Observed batches from one model at one
setting ranged from a clean integer staircase locked to position to genuinely independent scoring.
D43 drops `rank` from the interchange for that reason, and Phase 16 carries it out.

*Which is why the history is sent in full with every batch, and why `generatedAt` moves to the
end of the payload:* local servers reuse the cached state of an identical prompt prefix, so an
invariant prompt-and-history costs almost nothing after the first batch — but only if nothing
varying appears near the top of the document.

**Staleness per D39.** Off by default, for the reason sync is: a scheduled run is a thing the
user turns on having read what it does, and this one spends their electricity. `Scoring:Enabled`
is the kill switch, mirroring `Sync:Enabled` and for D20's reason.

**Interactive runs win.** A single gate around outbound scoring calls, which the sweep checks
between batches and stands down for — it resumes next tick from wherever it stopped, so nothing
is lost, and the person waiting waits for one batch rather than an hour. The same gate covers two
browser tabs pressing the button at once.

**A run says which route produced it.** `ProviderName` takes a third value, so the runs list can
tell a scheduled sweep from a manual paste and the card can report when the sweep last ran and
what it did. An unattended job that leaves no trace is indistinguishable from one that never ran.

*Two things this section did not anticipate, both found by building it:*

**A batch that will not fit halves rather than failing.** 8b gave `TooLarge` its own value on the
strength of a real refusal — a request too long for the model's context, which is an input
failure where `Truncated` is an output one. It is the only failure a sweep can act on by itself,
so it does: the batch halves down to a floor of five and the attempt is not counted against the
error budget, because the next one asks a different question. Without it a model with a small
context would fail three times and stop, every night, having ranked nothing.

**How much is left is a count, and so is what has been done.** D39's rule needed a read half
before 8d could pick batches from it, and the Recommendations page needed the same numbers to
say whether the work was finished — so `GetCoverageAsync` landed in 8c and both use it. What the
page reports and what the job does therefore cannot describe different backlogs, which is a
stronger property than either would have had alone.

*Also worth recording, because it shaped the query:* SQLite can neither order nor compare a
`DateTimeOffset`, which `BuildRequestAsync` already worked around by ordering history on
`DateOnly` and `Id`. There is no such stand-in for "when was this rated", so both halves of the
staleness rule are decided in memory over one column of the ranked backlog.

### Phase 9a — Cover art

> **Split out of Phase 9 by D47, and it is the half with a page at the end of it.** What was one
> phase ended at "art cached under `/data`", which is a cache nothing reads. This one ends on the
> backlog. It needs no external dataset and no licence, so it does not wait for 9b.

The covers AniList already publishes, fetched by a background job, cached under `/data`, served
by AniQueue, and rendered. D25's middle tier, which §10 called the real work and priced.

**The job is D25's shape and Phase 6's pattern.** It gates on its own precondition — a title with
a remote URL and no cached file — is paced, carries a budget, wakes on the `ILibraryChangeNotifier`
broadcast (D28, D41), and is a no-op when there is nothing outstanding. Registering it gives it a
row, a *Run now*, a cancel, a toggle, a cadence and a recorded history without writing any of that
(D40). It works through queued titles first, then planning, then everything else, which is
precondition ordering rather than orchestration: remove the ordering and it still converges.

**`AnimeImage` arrives here, and `Anime.CoverImageUrl` leaves** (D47, §4). D25's second schema
warning said more than one image per title kills that column; it turns out the first one does,
because the column is written through a merge that preserves what is already there. One row per
title per kind per source, holding the remote URL, the content hash and the failure state. Only
`Poster` rows exist in this phase.

**Served, never hotlinked**, which is the whole reason `ICoverImageResolver` was drawn — §10
measures the four ways rendering AniList's URLs directly fails. `/covers/{id}/{hash}` with a
year's `max-age` and `immutable`, streamed from `<data>/covers/`. Replaced art changes hash and
therefore changes URL, so a browser is never stale and never revalidates.

**What renders is a thumbnail column on the backlog and on Up Next**, one shared component, with
`CoverImageColor` behind it as the fallback — six bytes at 92% coverage, banked in Phase 6 for
exactly this. A title with neither gets a neutral block. The image is decorative in the
accessibility sense, because the title is in the adjacent cell and announcing it twice is worse
than not announcing it; explicit dimensions and lazy loading keep a 50-row page from shifting
under itself.

**Enrichment stays unauthenticated** (D25), and still may only add: it never writes status,
progress or score. A failed cover logs and shows nothing; what the pass fetched and what it could
not is on the task's own row (D40).

**The migration lands two weeks before Phase 11 squashes them**, which is what makes adding a
table and dropping a column here cost nothing to carry.

### Phase 9b — Id mapping and richer artwork

> **Unblocked by D46, and the blocker's answer changed the dataset.** `Fribb/anime-lists` has no
> licence at all, so it is not usable; `Kometa-Team/Anime-IDs` is MIT and takes its place, at the
> cost of a TMDB column. Held only on API terms now, not on a licence.

Cross-service identifiers, and the art they unlock, layered over the covers 9a already shows.

**Identifiers get their own table**, not `AnimeExternalId` — D25's first schema warning. They are
many-to-one and meaningless without the season the dataset supplies alongside them, so storing
them as peers of an AniList id would claim an identity they do not have. D46's dataset carries
`tvdb_season` and `tvdb_epoffset` on the record, which is what that warning asked for.

**Films route through IMDb.** The dataset publishes no TMDB ids, but 79% of films carry an IMDb
one, and TMDB's `find` endpoint resolves that to a film exactly rather than by matching titles —
one extra request per film, and the only path to film art that does not guess.

**Nothing orchestrates the sequence, and that is the point** (D25, D28). The id-mapping job takes
titles with no mapping; the artwork job takes titles that have a mapping and no cached image of
some kind. Both wake on the broadcast, both are no-ops when their input is empty, and sync →
mapping → art therefore happens in that order because of data readiness rather than because
anything sequences it. Remove the broadcast and it all still works, one tick later — that remains
the test of whether it has become orchestration.

**Additive by construction.** AniList covers 100% of titles and 9a already renders them, so
richer art layers over a base that is always present and the 69% that maps degrades into the 100%
that does not need to. Graceful degradation falls out of the data rather than being designed.

**What is still unverified is the reason this waits:** the TMDB, TVDB and fanart.tv API terms, key
requirements and rate limits (D46). The dataset question is closed; these are not.

### Phase 10 — Settings page

> **Smaller than it was, and split.** `Phase 10a` takes the per-source move out of it and runs
> first, ahead of Phase 15 (D36). The single task cadence and the per-task toggles have found a
> home on the tasks page rather than here (D40). What is left is the display preferences still in
> `ProfileSettings` — theme, date format, default queue size, backlog defaults — and showing
> operator configuration read-only.

Creates `/settings` as the one place preferences are changed. Phase 8 was to have created it and
does not (D35) — remote scoring lives on a card beside the route it serves, in the shape Sources
already uses — so this page starts from nothing rather than expanding something. Its contents are
unchanged, and it still has to hold two kinds of setting without letting them look alike (D36):

- **Preferences**, on `ProfileSettings`, edited here: displayed title language — moved from the
  Sources page, where Phase 5b left it sitting under a source it has nothing to do with (D22) —
  theme, date format, default queue size, and the backlog's default sort and filters. The last
  of those has no columns yet and needs a migration; Phase 11 squashes the history immediately
  afterwards, so adding one here costs nothing.
- **The per-source sync settings move to `userconfig.json`** — schedule, absence policy,
  conflict policy and precedence. D36 assigned them to the file and 8a deliberately did not move
  them; the same migration that adds the sort and filter columns drops `SourceSyncSettings`. It
  has to answer what a flat file does with a `(ProfileId, Source)` key, which is the one-profile
  question D36 already accepted for scoring, arriving somewhere it is load-bearing.
- **A register of everything in `userconfig.json`**, and where each value came from: the AniList
  account, the model endpoint, sync frequency, absence policy, the scoring schedule. D36 makes
  these editable, but on the cards that use them — the point of this half is not a second set of
  controls (D30) but the one place that answers "what is actually in effect, and which file do I
  edit when the pages cannot be reached". Naming the file and quoting the effective value is what
  makes a misconfiguration diagnosable without shell access, and it is the answer to the failure
  mode a layered settings system otherwise has: a value set in two places and nothing saying
  which won.

Destructive actions confirm explicitly. The accessibility and responsive passes happen here,
after Phase 9, so they run against the layout that ships rather than one about to gain images.

### Phase 11 — Docker and README
Multi-stage Dockerfile (SDK build → `aspnet` runtime, no SDK in the final layer), non-root
user, `/data` persistence, configurable port defaulting to 8080, `/health` endpoint,
compose health check, environment-variable configuration. README per brief §35, explicitly
explaining that v1 AI recommendation works **without giving AniQueue an API key**.

**Squash the migrations into a single baseline — and this is the last moment it is possible.**

By this point development will have accumulated several migrations, including at least one
that creates a column a later one drops. Collapsing them into one `InitialCreate` that
describes the shipping schema is standard practice before a first release, and it leaves
the operator of a self-hosted application with a migration folder that reads as a schema
rather than as a diary.

It is free **only while no database but ours exists**. After anyone else runs AniQueue their
`__EFMigrationsHistory` names migrations that would no longer exist, and startup would try
to apply a baseline over a populated schema and fail. There is no way back from that except
asking users to delete their data, so the window closes the moment an image is published or
a release is tagged.

The procedure, and the reason it is low-risk:

1. Delete `Persistence/Migrations/` and the development database.
2. `dotnet ef migrations add InitialCreate`.
3. Run the tests. `SqliteTestDatabase` applies migrations rather than calling
   `EnsureCreated`, so a broken baseline fails the whole Infrastructure suite immediately
   rather than at someone's first run.
4. Start a container against an empty volume and confirm the schema is created.

Skipping it costs almost nothing — a slightly longer history and one redundant column
create-and-drop. It is a tidiness measure, not a correctness one. But it can only be done
here, so it is listed as a gate rather than left to judgement.

Final gate: Release build, full test run, image build, `docker compose up -d`, health check
verified, **container recreated and the database confirmed intact**.

### Phase 12 — Optional single-user authentication
A login that can be switched on, off by default, and **off remains a supported deployment** —
this is a lock for people who want one, not a requirement discovered late. Single user only;
multi-user accounts, roles and per-profile libraries stay out of scope, and nothing here should
make them harder later.

**It fills the number D31 left vacant**, and lands before Phase 14 rather than after it, because
the security pass reviews §6's high-risk surfaces against the finished application and this is
one of them. Publishing has the same ordering for the same reason (D34): both clocks start on the
first tag.

**Several earlier decisions are conditional on it and say so.** D38's guards on the scoring
endpoint are cheap insurance while there is no trust boundary and become an actual defence once
there is; the capped diagnostic echo is the same. Nothing in Phase 8 assumes this exists, and
nothing in it has to change when it does.

**Enabling it is a setting like any other** (D36), which means the credential is the one value
that cannot follow the rule: a password is not written to a file in plain text and is not shown
back. Storing a hash in the database rather than in `userconfig.json` is the exception D36 has to
carve, and the settings file names the account without holding the secret.

**What it must not break:** the unattended jobs, which run without a session and must keep doing
so; `/health`, which a compose health check reaches before anybody has logged in; and the kill
switches, whose whole purpose is to work when the UI cannot be reached. A lock that locks the
operator out of their own escape hatches is worse than none.

### Phase 13 — Continuous integration
GitHub Actions: build and full test run on every push and every pull request, and a tagged
release on `main` that builds the multi-stage image from Phase 11 and pushes it to Docker Hub.
Registry credentials live in repository secrets and appear in no committed file.

**The first published tag is a one-way door, and two gates stand in front of it.** Phase 11's
migration squash is free only while no database but ours exists, and publishing an image ends
that. An image on Docker Hub is also the moment a defect stops being local. So the workflow is
built and exercised here, but **the first push of a release tag waits on Phase 14** — until
then CI builds the image and does not publish it.

Nothing about CI is allowed to become the only way to build or test: `dotnet build` and
`dotnet test` at a clean checkout stay the contract, and the workflow runs those rather than
reimplementing them.

### Phase 14 — Security pass and stabilisation
A deliberate pass over what §6 names as high-risk, against the finished application rather than
against each phase in isolation — which is the point of doing it last, and the reason it cannot
be distributed across the phases that created the surfaces:

- The model endpoint's outbound requests: that the address is operator configuration only, that
  a hostile or wrong endpoint cannot become a request to somewhere else, and that timeouts and
  response size limits hold.
- The import path: upload limits, secure XML settings, and the paste route into Phase 7's
  importer.
- The artwork cache's filesystem writes under `/data`, including what a remote filename is
  allowed to determine about a local path.
- Forwarded headers, error output in production, and the non-root container's permissions.

Plus whatever the bug list has accumulated. It gates Phase 13's first published tag.

### Phase 15 — Background tasks
Everything the application does on its own becomes visible, operable and recorded (D40), every
job learns to say what it changed (D41), and the modal wait in front of a model is deleted (D42).

**It runs next, before Phase 9**, and `Phase 10a` runs before it — see §7's preamble for why the
numbers and the order disagree.

**The five parts are ordered so that the deletions come last.** 15d and 15e remove surfaces that
are the only way to do certain things today, and removing them before the tasks page exists would
leave a commit that is buildable, green and unusable — a state §7's exit criteria do not catch.

#### Phase 10a — Per-source settings to the file
D36's dagger, discharged early because D40 depends on it. `SourceSyncSettings` moves wholesale
into `userconfig.json` and the entity, its table and its configuration are deleted; the primary
source becomes a single key naming a source rather than a rank per row. Touches `SyncService`,
`ImportService`, the Sources page and the seeder, which is exactly why it is not folded into a
tasks phase.

*Exit:* every sync setting is read from the file, the Sources card writes only there, the
migration drops the table, and the suite is green.

#### Phase 15a — The job contract
`IBackgroundJob` takes a `JobRunContext` — trigger and unit — and returns a `JobRunOutcome`. Jobs
expose the units they own, so `BackgroundJobRunner` iterates units and calls the job once per unit
rather than the job looping sources inside itself, and both backoffs are deleted.
`RelationBackfillJob` loses `MaxRequestsPerVisit` in favour of a time budget, because the ceiling
was never the rate-limit guard — `RelationPacing` is — and a manual run that stops half-way is
the behaviour this phase exists to remove. Every job publishes what it changed (D41).

Deliberately behaviour-neutral: the same schedules drive the same work, outcomes go to the log,
and nothing new is stored. That is what makes the next part's failures attributable.

*Two adjustments made while building it.* **The trigger channel and the per-run
`CancellationTokenSource` move to 15c**, where the registry that writes to them lives — plumbing
with no writer is dead code, and the phase is more honest without it. And **deleting the runner's
own backoff required recording a run that threw**, or a job that fails before it reports anything
leaves the cadence clock unmoved and throws again on the next tick forever; 15a logs that and 15b
gives it the row that actually moves the clock.

*Exit:* jobs run as before on the new contract; a run that found nothing is distinguishable from
one that was not due; suite green.

#### Phase 15b — Job runs
`JobRun` lands with pruning at two hundred rows **per unit**, so a source syncing hourly cannot
crowd out the history of one syncing weekly. Due-ness moves onto it, which is what makes "cancel
skips this cycle" true rather than aspirational — without it a cancelled run leaves no trace the
due check can see and the next tick restarts it.

`IBackgroundJob` gains a stable `Key` separate from its display `Name`: what a task is called is
a label somebody reads and may change, and a rename must not orphan the history it names.

**Both jobs were reading the wrong clock, in the same shape.** Sync took due-ness from `SyncRun`,
which is written only when a run reaches a terminal state; the sweep took it from the last
`RecommendationRun` a schedule produced, which means the last time it *applied* a ranking. So a
run that was cancelled, that failed, or that found nothing looked like a run that never happened,
and the next tick started it again — for the sweep, minutes of somebody's GPU. Reading from the
record every run writes fixes both, and returns `SyncRun` to being purely the library's audit
trail, which is what its own documentation says it is. `IRecommendationService.GetLastRunAtAsync`
is deleted with the question it answered.

*The single cadence moved to 15c.* Consolidating `Sync:AniList:Schedule` and `Scoring:Schedule`
here would delete the schedule controls from Sources and Recommendations one part before the page
that hosts the replacement exists, leaving a merged state with no schedule UI at all. Storage
belongs here; a setting and the surface that edits it belong together.

*Exit:* every executed run is recorded including the ones that found nothing and the ones that
threw; due-ness is answered from that record for every task; suite green.

#### Phase 15c — The tasks page
`/tasks`: a singleton registry each runner registers with, holding per-unit state, the started-at,
the trigger channel and the token source — the last two moved here from 15a, because this is
where the thing that writes to them arrives. The single `Tasks:Schedule` key replaces
`Sync:AniList:Schedule` and `Scoring:Schedule` here too, moved from 15b so that the setting and
the control that edits it land together. One row per schedulable unit with run now, cancel, an
on/off toggle writing to `userconfig.json`, the last run, its outcome and its failure reason in
plain words. One cadence control above them. A history card below, reading `JobRun`.

The page talks only to the registry and never to a job, so the runner's sequential loop stays the
only thing that executes anything. It stays live by subscribing to the registry's event, plus a
one-second timer *only while something is running* — so an idle page left open costs nothing, and
the subscription is disposed with the same discipline `BackgroundJobRunner` already documents for
`ILibraryChangeNotifier`.

Rows come from what is registered, so Phase 9's jobs appear as rows the day they are registered.

**The registry is two interfaces over one object.** `ITaskRegistry` is what a page gets — read the
rows, ask for a run, stop one. `ITaskRunnerBridge` is what a runner gets — wait for a request, and
say what is happening. A page must not be able to declare a run started, and a runner has no
business enumerating rows; splitting the surface is what makes that structural rather than a
convention.

**Three things this part got wrong first, all found by running it:**

- **Row order cannot come from registration.** Each job has its own hosted service and they start
  concurrently, so the rows came out in a different order on almost every boot. Sorting by key
  fixes it; sorting by name would have moved a row whenever somebody renamed a task, which is the
  same problem arriving more slowly.
- **A switched-off task must not offer a live button.** Every job checks its own switch before
  anything else, so *Run now* on a disabled row recorded nothing, changed nothing and explained
  nothing. Off means off, by hand as well as on its own, and the button says so by being disabled.
- **Relations needed a switch after all.** D40 predicted this and the reason held: a row carrying
  a button and no way to stop it invites the question. It lives in `Tasks:RelationsEnabled` rather
  than a `Relations` section of its own, because a section holding one boolean is a home built for
  a single tenant.

*Exit:* every task can be seen, started, cancelled and switched off from one page; a failing task
says why on its own row; one cadence drives them all.

#### Phase 15d — Scoring demolition
D42. The *Rank now* button, `RankRemotelyAsync`, the cancel, the soft guard, the previous-run
duration display, the remote branch of the preview, `IScoringGate`, `ScoringGate` and their tests
are deleted. `BusyDialog` loses `OnCancel`. *Test connection* is disabled while the scoring task
runs and says so, which is the whole of what replaced the gate.

**Roughly four hundred and fifty lines net, almost all of it deletion.** The `ScoringRoute` enum
goes with the route: with one way for a ranking to arrive at this page, every surface that asked
which had produced a preview stopped asking, and `ProviderName` becomes a constant. Old runs keep
whatever value they were written with, so *Past rankings* still distinguishes them and nothing
rewrites history.

**Two things this turned up that were not on the list.** The sweep's class documentation still
said *"it does not wake on library changes"* — true when 8d wrote it and false since D41 changed it
in 15a, where the code was updated and the paragraph above it was not. And `BusyDialog`'s cancel
had left a shape worth keeping the reasoning for: it latched to *Stopping…* on the first press
because cancelling is not instant, which is exactly what the tasks page now does for the same
reason.

*Exit:* no outbound scoring request exists that anybody is waiting on; the paste route and the
sweep both still produce and apply a ranking.

#### Phase 15e — Sources reshape
The sync fetch modal goes. Sources keeps AniList configuration, the MyAnimeList file import and
its dialog, and gains one *Review held changes* button — shown only when a background run held
something, fetching inline with a spinner in the card rather than an overlay. D21's review is
otherwise untouched, including that it persists nothing, which is exactly why there is a button
here at all rather than a stored list.

*Refresh related titles* is replaced by *Delete all title relationships*, and the rename is the
point: that button forgot every marker and immediately ran a pass, which made sense while this
page was the only way to make anything happen. The tasks page runs passes now, so what is left is
the destructive half — and a button called *Refresh* that quietly empties a table is worse than
one that says so and asks. It confirms in place rather than behind a dialog, so the sentence
explaining what is about to happen and the button that does it can be read at once.

**Nulling the markers is not optional**, and it is one transaction with the delete for that
reason: without it every title reads as already fetched and nothing rebuilds the graph until
`StaleAfter` expires thirty days later — a button that silently emptied the relation graph for a
month. `IRelationBackfill.RefreshAsync` is deleted; `ForgetAsync` replaces it and does not fetch,
because refilling is the ordinary pass and belongs to a task.

*Confirmed by running it:* deleting removed four edges and took relation coverage from "all 5
titles" to "0 of 5", which is the marker half being visible rather than assumed.

*Exit:* Sources is configuration, one review button and a file import; the relation graph can be
emptied and rebuilt from the tasks page.

---

**Phase 15 is complete here.** What it cost, gathered in one place: `IScoringGate`, both backoffs,
two per-feature schedules, `ScoringRoute`, `RefreshAsync`, `GetLastRunAtAsync`, `BusyDialog`'s
cancel and the whole interactive scoring run. What it added: one page, one cadence, one record of
what background work has done, and a contract that makes the next job additive.

---

### Phase 16 — Scoring without a rank

Carries out D43. One field leaves the interchange, and four places stop referring to it.

**The prompt stops asking.** `ScoringPromptBuilder` drops `rank` from the worked example and drops
the reply rule that governs it — *"\"rank\" starts at 1 and each is used once"* — while
`predictedScore` and `confidence` keep theirs. The limited form keeps *"return exactly N"* and
loses *"ranked 1 to N"*. The word "rank" survives in the prose that describes the **task**
("Rank these", "Rank every candidate"), because judging candidates against each other is still
what is being asked for; what goes is the request to number the result.

**The parser stops requiring it.** `ScoringResult.Rank` goes, and with it the duplicate-rank check
and the gap detection, which exist only to police a numbering. `parsed.OrderBy(r => r.Rank)`
becomes an order by `predictedScore` descending, so a preview reads in the order the backlog will.
`RecommendationService`'s unknown-id message stops naming a rank and names the position in the
reply instead — the reply is what the user would have to look at.

**A `rank` that arrives anyway is ignored, not rejected.** A model repeating a shape it has seen
in training is not returning a malformed answer, and failing a batch over a field nothing reads
would be a self-inflicted spend of the sweep's error budget.

**The column goes, in a migration.** `RecommendationRunItem.Rank`, the `(RunId, Rank)` index and
the `CK_RecommendationRunItems_RankPositive` check constraint. SQLite cannot drop a column from a
table carrying a check constraint, so EF rebuilds the table — ordinary here, and the reason to run
the migration against a copy of the development database rather than only against a fresh one.
Existing rows keep their scores, confidences and reasons.

**Two displays go.** The backlog's *Ranked N of M* fact, which was showing a number meaningful only
inside a batch the user cannot see, and the rank column in the Recommendations preview table.
`ScoringDetail.CandidateCount` loses its only reader and goes with them; the run-level *"R of C"*
in *Past rankings* is a different field and stays, because how many candidates came back is a fact
about the run rather than about a title.

**Verified by running it, not by compiling it.** A sweep against a real local model after the
change, with the returned scores read out of the log: the check is whether the staircase D43
records is gone. A green suite proves the field is gone, which is the easy half.

*Three things this section did not anticipate, all found by building it:*

**The `(RunId, Rank)` index is replaced rather than simply dropped.** It was the foreign key's
index with a sort key appended, so removing the sort key leaves EF creating a plain
`IX_RecommendationRunItems_RunId` of its own. Nothing declares it and nothing should: declaring
one in `RecommendationRunItemConfiguration` would duplicate what the FK already gets.

**The scaffolded `Down` could not run.** EF re-adds a dropped non-nullable column with
`defaultValue: 0`, then re-adds `CK_RecommendationRunItems_RankPositive`, which requires `>= 1` —
so on any database holding run items the generated revert fails against its own constraint. It is
corrected to 1 by hand. Worth knowing generally: **when a dropped column carried a check
constraint, read the scaffolded default against that constraint before trusting `Down`**, because
nothing in a forward-only test will ever exercise it.

**A staircase is not what the evidence looks like; ties are.** "Read the scores out of the log"
under-specifies the check, because the parser now orders results by score, so the stored order is
descending whatever the model did — the sequence being descending proves nothing. What does
prove something is **repeated scores**: a score derived from a unique rank cannot produce two
titles with the same number. The verifying sweep returned three titles at 8.5, three at 6.5 and
two at 5.0 in one batch, which no position-derived scoring can produce.

*Exit:* no request asks for a rank, no reply needs one, no row stores one and no page shows one;
the backlog sorts by the only number the model is now asked to produce.

---

### Phase 17 — Improve remote model scoring

**A collection point rather than a plan, and deliberately so.** D45 made the remote route
experimental and opt-in because three models were tried and two could not answer at all. Everything
below was found while establishing that, and none of it is worth building until there is a wider
sample of models to build against — several of these have a right answer that depends on what
"typical" turns out to mean. The phase exists so the findings are not re-derived.

**Ordered by whether the application is currently telling the truth**, which is a different order
from how much each one hurts.

#### 17a — A failed batch is skipped, which it currently is not

`ScoringSweepJob` says *"a failed batch is recorded and skipped rather than ending the sweep — one
odd title must not block everything behind it"*. **Nothing skips.** A failed batch applies nothing,
so its titles keep a null `RecommendationUpdatedAt`, and `ChooseAsync` orders never-scored first
with `AnimeId` as a tiebreak — chosen deliberately *"so two runs over an unscored backlog take the
same titles rather than an arbitrary overlap"*. That stability is right everywhere else and a trap
here: the next batch re-selects **exactly the same titles**, so three consecutive failures are
three attempts at one request.

The consequence is the failure the comment promises cannot happen. One title that breaks a reply
sits at the front of the never-scored ordering permanently, is in every batch of every sweep, and
the backlog behind it is never reached.

**Rotate on failure.** A failed batch advances an offset into the neediest-first ordering, so the
next batch takes the *next* N rather than the same N. No persistent state — the offset lives for
the sweep. It needs no new column and no attempt counter, and it makes the error budget mean
something it does not mean today: three failures become three different questions, which
distinguishes one poisonous title from a model that cannot do this at all. A model that genuinely
cannot still fails three times and stops, exactly as now.

#### 17b — One sweep is one run

A sweep produces one `RecommendationRun` per batch. Pressing *Run now* once and getting ten rows
in *Past rankings* does not match anybody's idea of a run; a measured evening produced 27 rows from
about four sweeps.

**Group the record, and do not buffer the apply.** Deferring every batch's scores to the end of the
sweep was considered and is wrong: batches are independent questions over disjoint candidates, so
there is no consistency between them to protect, and holding them would put an hour of model work
at the mercy of one late failure — which is exactly what the resume-from-where-you-stopped design
and the error budget exist to avoid. What is wrong is the *reporting*, not the writing. A run
should own its batches rather than a sweep minting peers.

#### 17c — A history that outgrew the context is unrecoverable

`TooLarge` halves the batch. The batch is the **candidates**, which are 5.4% of a request; the
history is 94.6%. Halving ten to five saves roughly 720 tokens of 26,500, so a history-driven
overflow cannot be rescued: the batch reaches `MinimumBatchSize`, still does not fit, and every
retry meets the same wall for as long as the setting stands.

Measured, so the arithmetic is not guesswork: **~44.5 tokens per rated title** and ~144 per
candidate. Against a 49,152-token context with 3,248 reserved for output, the wall is near **990
rated titles** — reachable by anybody who keeps raising `Scoring:HistorySize` in step with their
library, which is the natural thing to do and currently reads as a free improvement.

The fix acts on the term with the leverage, and the tension to resolve is that `RunBatchAsync`
deliberately uses the user's own `HistorySize` *"because a sweep predicting against different
evidence from a manual run would give two answers to one question"*. Whatever reduces the history
must do it **between sweeps rather than within one**, or it undoes the snapshot that makes a
sweep's batches comparable with each other.

#### 17d — A cap on the scored set for unattended runs

The idea this phase was collected around: an unattended sweep that fails is worse than one that
scores slightly less well, because nobody is there to notice, and AniQueue cannot know what model
is behind the endpoint. A cap trades accuracy for a failure rate — bounded, reversible, and aimed
at the thing 17c makes unreachable.

Two things to settle with it rather than assume:

**The accuracy cost is measurable.** Score the same candidates at 200 and at 564 and compare the
numbers. Diminishing returns are likely — the most recent 200 rated titles plausibly capture taste
about as well as 564 — and if that holds the trade is far cheaper than it sounds.

**A cap may not be the mechanism at all.** `Scoring:HistorySize` already defaults to 200; the
measured library had been raised to 564 by hand. So what is missing may be that nothing tells
somebody a larger history is a reliability risk rather than a free improvement, which is a smaller
change than a second setting.

**What a cap does not fix**, recorded so it is not expected to: gemma-4-12b failed at a history of
**fifty**. Where a model's failure is that it recites the history before answering, less history
means a shorter recital and not a different outcome.

#### 17e — Notes that need no work yet

**"Answered with an empty message" is true and useless.** A model can return an empty `content`
with a full `reasoning_content` — gemma did, repeatedly — having spent its whole allowance
thinking. That is a distinct and actionable failure ("your model never began the answer") and it
currently reads as though the server misbehaved. The truncated message was fixed for exactly this
reason; this one was not.

**A short reply is usually a choice, not a limit.** Replies of five results out of ten came back
with `finish_reason: "stop"` having used ~550 of 1,712 tokens. The prompt permits stopping early
on purpose, so this is the permission being taken rather than a budget problem, and it is the
largest remaining source of unscored titles on a model that otherwise works. Whether completeness
can be improved without removing an escape hatch that keeps small models usable is open.

**The prompt cache is not AniQueue's to win, and this is the evidence.** Two consecutive requests
were measured byte-identical for 94,758 of ~96,500 bytes — 98.2%, diverging only at `generatedAt`
where the writer puts it. llama.cpp recognises the prefix, routes deliberately to the warm slot at
`f_sim_best` up to 0.999, and reprocesses all ~26,000 tokens anyway. Neither slot count nor
`kv_unified` changed it; single-slot and a smaller context cut prompt processing from 9.3s to 3.3s
by freeing the GPU, not by reusing anything. **Recorded so nobody investigates the payload again**
— if the tokens are still being reprocessed, it is the server.

**Which models work is the open question D45 turns on.** Three is enough to stop promising the
route works and not enough to say which models do. The Experimental badge comes off when a wider
sample says something more useful than "it depends", and D44's citation field wants measuring
against the same sample.

*Exit:* a sweep gets past a batch it cannot score rather than re-asking it; one sweep is one row
in *Past rankings*; and no setting a user can reach makes the sweep fail in a way it cannot
recover from.

---

## 8. Test plan

Allocated to whichever project can run each test fastest.

**Core.Tests — no database, milliseconds.** MAL XML parsing; malformed XML; `0000-00-00`;
XXE rejection; status mapping; JSON schema validation; AI result validation (unknown
candidate, duplicate, missing candidate, rank collision, out-of-range predicted score,
out-of-range confidence); runtime calculations including unknown-duration cases and partial
sums; scoring-response validation against the Phase 7 schema.

AniList parsing is tested the same way, against a committed JSON fixture: every scoring system
normalising into 1–10, a `POINT_100` score below 5 clamping to 1 rather than to null, `REPEATING`
mapping to `Watching`, partial and wholly-null FuzzyDates becoming null, a missing `english` title
falling back rather than writing null, a missing `idMal`, one media id appearing in two lists, and
a GraphQL `errors` array arriving with HTTP 200 being treated as a rejection rather than an empty
list.

**The fixture is structurally faithful and its content is fictional.** §0 forbids committing a
personal export to a public repository, and a captured response *is* one — so the shape is copied
from a real response and the titles, ids, scores and dates are invented. That is not only a
compliance point: a hand-authored fixture can contain the cases a real library does not. The
library used to verify the API held no `PAUSED`, `DROPPED` or `REPEATING` entry, no partial date
and no custom list, so a capture would have tested none of the mappings most likely to be wrong.

**Infrastructure.Tests — real EF, real SQLite.** Use `Data Source=:memory:` with a
**deliberately held-open connection** (the database dies when the last connection closes).
The EF `InMemory` provider is not used at all — it does not enforce the constraints under
test. Covers: migrations apply cleanly; dedup on external identity; import
idempotency; import preserves local fields; **queue reorder edge cases and the contiguity
invariant** (load-bearing per D2); completion transitions; applying AI recommendations leaves
`QueueItem` untouched.

Phase 5 adds, and these are load-bearing rather than decorative:

- **The bridge.** An AniList entry carrying `idMal` matches a MyAnimeList-imported row instead of
  conflicting with it, and writes the AniList identifier so it never conflicts again. This is
  the test that stops D17 regressing into 750 conflicts.
- **Precedence.** A lower-ranked source cannot overwrite status, progress or score written by a
  higher-ranked one, but can still fill catalogue metadata (D18).
- **Absence scoping.** A row with no identifier for the syncing source is untouched by any
  absence policy; a row that carries one and is missing from the fetch is acted on. A partial or
  empty fetch acts on nothing (D19).
- **Deleting an entry deletes its queue slot**, because a slot whose entry is gone can never be
  released (D19).
- **Two writers.** A sync commit and a queue reorder issued concurrently both succeed and leave
  positions contiguous — D2's invariant under the one condition D2 never faced.
- **The client's failure paths**, against a stub handler: a 404 that names the private-account
  case, a 429 that says how long to wait, a socket error that does not repeat the resolved host
  back at the user, and a server claiming `hasNextChunk` forever failing rather than looping.
- **The run record.** A failed fetch and a list that already matched each write a row; a preview
  still awaiting a decision writes none; the kill switch writes none.
- **The unattended subset.** Creates and updates commit; conflicts are held; a source set to ask
  first writes nothing and records `HeldForReview` with a count. Linking by exact title converges
  — the second run has nothing left to conflict about — and an ambiguous match is never linked
  whatever the policy says.
- **Scheduling, without a clock.** Due-ness and backoff are arithmetic over the status the sync
  service reports, so the job is tested against a stub: off never runs, a source that has never
  run is due immediately, each schedule waits its interval, and a failing source is retried at
  double the interval per failure up to sixteen times it. Scheduling that needed real time to
  test would be scheduling nobody could test.

Phase 6 adds:

- **6a proves a subtraction.** Nothing new is asserted; the franchise tests are deleted with
  their subjects rather than ported. What must stay green is the migration applying to a fresh
  database and to a pre-D15 one — `FranchiseExpansionMigrationTests` now upgrades through both
  the expansion and the drop in one run, seeding entirely in SQL because the current model can
  express none of it.
- **Parsing relations**, in Core against a committed fixture that is structurally faithful and
  fictional per §0: relation types mapped, unknown types dropped rather than stored as `Other`,
  manga nodes filtered out, a missing `startDate`, and **edge direction preserved as fetched**.
- **The backfill's laziness.** A title with no edges is still marked and never refetched; a
  second run writes no duplicates; the kill switch stops it; batching splits at 50; pacing is
  arithmetic over `TimeProvider`, so no test sleeps.
- **Re-reading, and what it is allowed to delete.** An answer a day short of thirty days is still
  trusted and one a day past it is not; an edge the source no longer publishes is removed; an
  edge belonging to a title the response never mentioned is kept; a failed re-read deletes
  nothing at all. The clock is moved by a stub rather than waited on.
- **What an expansion shows.** Counts exclude hidden entries and include every other status;
  only owned relatives are counted, so the badge never promises more than it opens; ordering is
  by release date with unknowns last; a relation read from the far end is inverted; the same
  pair stated from both ends counts once; two ends that disagree are labelled "Related" rather
  than arbitrated; nothing is ever transitive beyond one edge.
- **The sequel walk.** It traverses *through* a Completed middle season without queueing it,
  and through a season the library does not own at all; it never goes backwards; it appends in
  release order rather than the order it found things; a recap or compilation in the middle of
  the chain is passed through rather than queued; a hidden season carries the chain without
  being queued; a cycle terminates; it reports `QueueAddResult` categories correctly, is a no-op
  when re-run, and leaves positions contiguous. The count behind the button reports what the
  press would actually append, and a title with no AniList identifier has no chain at all.
  Tested against the real `QueueService` rather than a stub, because the hand-off to
  `AddAnimeAsync` is the seam the design turns on.

Phase 9a adds:

- **The gate, which is most of the job.** A title with no remote URL, one already cached, one
  marked permanently failed and one that has spent its five attempts are all skipped; a title
  whose `RemoteUrl` has changed is picked up again despite either failure state. A pass with
  nothing outstanding makes no request at all, which is D25's "idle when its input is empty"
  stated as an assertion.
- **Disk wins, and the test is that it heals.** A row claiming a cached file whose file has been
  deleted is refetched rather than served; a file with no row is deleted by the same pass.
- **The guards, against a stub handler.** A host off the allowlist is never requested; a redirect
  is not followed; a non-image content type, a body over the cap and a 404 each record a
  *permanent* failure; a timeout, a 5xx and a 429 each record a transient one and count toward
  five. The stub observes its cancellation token — a cancelled pass that appeared to succeed
  would hide the behaviour the test exists to check.
- **A half-written file is never served.** A fetch cancelled or failed mid-write leaves nothing
  under the final name, because the write goes to a temporary file and is renamed.
- **The resolver, in Core and with no database.** An image row with a content hash resolves to a
  served URL; a title with no cached image but a colour resolves to the colour; a title with
  neither resolves to nothing. This is the whole of what the two pages call.

No test may depend on a live external API.

**One SQLite trap worth knowing before Phase 7 meets it, and it is wider than first recorded.**
SQLite cannot `ORDER BY` a `DateTimeOffset` — EF stores it as text with an offset and refuses to
sort it, throwing at query time rather than returning a wrong order. `SyncRun` reads recency from
its key instead, which is the same order for an append-only table. `RecommendationRun` browses
newest-first over `CreatedAt` and will hit exactly this.

**Comparison fails too, which Phase 6b found the hard way.** A `WHERE x < @cutoff` over a
`DateTimeOffset` does not throw at run time — it fails at *translation* time, with "could not be
translated", so it is a bug that survives compilation and every test that does not exercise that
query. The staleness check on `AnimeExternalId.RelationsFetchedAt` needs exactly that comparison,
which is why that one column is a `DateTime` while every other timestamp in the model is a
`DateTimeOffset`. **The rule: a timestamp a `WHERE` must compare is a `DateTime` in UTC; a
timestamp that is only read, displayed or null-checked stays a `DateTimeOffset`.** Filtering in
memory instead was the alternative, and it was declined — §6 requires the filtering to happen in
the database.

---

## 9. Risks

**A DOM-rewriting browser extension breaks Blazor, and one popular one did.** Dark Reader's
dynamic theme rewrites `<head>` as the page loads and then watches for changes. Blazor's renderer
holds live references to the nodes it created, so a node moved or removed underneath it fails the
next render: `TypeError: … insertBefore, n.parentNode is null`, then `No element is currently
associated with component 1`, and the circuit dies on load. Toggling the extension off makes it
unreproducible; toggling it on reproduces it immediately, in both Firefox and Chromium.

**This is a known upstream limitation, not a misconfiguration here.** The same failure is reported
against Edge's translation feature (dotnet/aspnetcore#47111, closed as *not planned*) and against
Chrome 123 (#55085, closed as *External*), alongside a long line of "No element is currently
associated with component" reports (#5592, #10715, #51393, #51825). Anything that mutates the DOM
Blazor owns can break it, and the framework does not defend against it. The comparison that made
this obvious: the same machine and browser runs this author's portfolio site untroubled, and that
site is MVC — it renders HTML once and never binds to it again.

**The fix is `<meta name="darkreader-lock">`, which is the extension's own opt-out** for sites
that already have a dark theme. AniQueue does, behind `prefers-color-scheme`, so the extension was
re-theming a theme. In Dark Reader's source the tag is checked *before* anything runs: with the
lock present it never starts its theme or its watchers, so there is no mutation to lose a race
against. Verified here in Brave and Firefox with the extension installed, against the page that
previously failed on every load.

**Prerendering was turned off and then back on**, which is worth recording because the reasoning
looked sound and was wrong. With no prerendered DOM there is no attach step to corrupt, and it did
fix Firefox — but Chromium still failed, because the extension keeps mutating long after the
attach. A change that costs an empty first paint on every load for every user, and buys immunity
in one browser, is not worth keeping once the real fix exists. An earlier `defer` on the Blazor
script was reverted for the same reason: instrumenting the page showed the component-state comment
being consumed well before the attach in the very runs that failed.

**The technique is the transferable part.** None of the above came from reading code. A
`MutationObserver` installed as the first element in `<head>`, posting every node removal with
timings to a temporary endpoint that appended them to a file, is what named the culprit — the
extension's fingerprints sit between `LOAD` and the attach in the resulting log. The browser
console is no use when the symptom only appears on someone else's machine.

**What is still true and unfixed:** any other extension that rewrites the DOM — a translator, an
accessibility overlay — can break the same way, and there is no lock tag for those. If that ever
reaches a user, the lever is `prerender: false` on the interactive root, which removes the attach
step at the cost of first paint. It is not enough on its own, as above, but it is the only
structural defence available.

**Phase 11's README should mention the class of problem**, because a self-hoster meeting a blank
page has no way to guess that an extension caused it.

**SortableJS vs Blazor's DOM ownership — the real one.** Blazor's renderer diffs against
its own virtual tree; SortableJS physically moves nodes behind its back, so the next render
can duplicate or resurrect rows. The working pattern is `@key` on every item plus reverting
the DOM move inside `onEnd`, then calling into .NET with `(oldIndex, newIndex)` and letting
the re-render produce the authoritative order. Budget a spike in Phase 4. Mitigated by D5.

*Resolved in Phase 4, and the pattern above held exactly as written.* The implementation is
`Components/Pages/UpNext.razor.js`, which is commented as the reference for it. Three things
were learned that the paragraph above does not imply:

- **The drag never edits the list.** It reports two indices and nothing else. Because the
  DOM is reverted before .NET is called, the visible move is always produced by the server's
  re-render — so a reorder the service clamps or rejects cannot leave the page showing an
  order the database does not hold. This is what makes the risk tractable rather than merely
  survivable.
- **Drag must be allowed to fail without taking the page with it.** A failure to initialise
  the interop throws inside `OnAfterRenderAsync`, which tears down the circuit and shows the
  user "An unhandled error has occurred" — a strictly worse outcome than having no drag. It
  is caught and logged, and the page degrades to the move buttons, which is what D5 chose
  them for. Anything layering interop onto a page should do the same.
- **`Assets[...]` returns an application-relative path, which is not a valid ES module
  specifier.** `import()` rejects it outright. Resolve through `new URL(path,
  document.baseURI)` rather than prefixing a slash, so the application still works when
  hosted under a sub-path behind a reverse proxy.

Still open, because it cannot be verified from a desktop browser here: touch. The
configuration disambiguates drag from scroll with `delayOnTouchOnly`, which is the
conventional answer, but it is untested on a real touch device.

**Non-root container against a bind-mounted `/data`.** A non-root UID cannot create the
database file in a host directory owned by root. Named volumes are fine — Docker seeds
ownership from the image — bind mounts are not. Plan: pin a known UID/GID in the image,
default compose to a **named volume**, and document `chown` for bind-mount users (the
common Unraid case).

**Privacy-hardened browsers break Blazor Server's DOM contract.** Narrowed by bisect: the
circuit dies with `Cannot read properties of null (reading 'insertBefore')` followed by
`No element is currently associated with component 1` in **Brave**, while Firefox and Edge
are clean. Edge is also Chromium, so this is Brave's Shields layer, not the engine.

**It is intermittent.** Toggling Shields off and back on for the site stopped it recurring,
which points at cached per-site Shields state or a stale cosmetic-filter list rather than a
deterministic rule. Do not expect to reproduce it on demand — an attempt that comes up clean
does not mean it is gone.

Blazor Server patches the live DOM through direct node references. Shields' cosmetic
filtering hides and sometimes removes nodes, and its fingerprinting protection patches
native DOM APIs — either can invalidate a reference the renderer still holds, after which
the next render batch fails and the circuit tears down.

This matters beyond development. A self-hosted anime backlog manager skews heavily toward
the home-server audience, which skews heavily toward Brave; those users would see only
"An unhandled error has occurred" with no explanation.

Not yet mitigated. Options, in increasing order of cost:

- Document it, and tell users to drop Shields for their AniQueue host.
- Rename any CSS classes that generic cosmetic-filter lists target.
- Disable prerendering on the root components, removing the phase where the client adopts
  server-rendered markup. Only a partial fix — a node removed later still breaks a
  subsequent patch — and it costs a blank first paint.

Reassess once it is known *which* element Shields is acting on.

**SQLite single-writer.** Adequate for one user, but a concurrent import and queue write
can hit `SQLITE_BUSY`. WAL plus a `busy_timeout` at startup; keep write transactions short.

*Phase 5c makes this real.* Until then every write is user-initiated from a circuit, so two
writers required two people. A scheduled sync writes with nobody present, and it contends with
the user over the same narrow table — its `AdvanceAsync` and a drag in Up Next both rewrite
`QueueItem.Position`. The mitigation is the one already chosen rather than a new one: WAL and a
30-second `busy_timeout` against millisecond writes, a commit skipped entirely when nothing would
change, and ticks that cannot overlap. An application-level lock was considered and declined —
it is a single-process guarantee that silently becomes wrong the day anything else opens the
file, which is a worse failure than the one it prevents.

**Scope.** This is a large MVP — 25 acceptance criteria across 11 phases. Treating them as
one milestone is the main schedule risk; the phase ordering exists to avoid it.

---

## 10. Out of scope

**Not in MVP** (brief §41): MAL/AniList OAuth, live two-way sync, built-in OpenAI calls,
Ollama integration, user registration, social features, comments, public profiles, mobile
native apps, automatic metadata scraping, franchise grouping of any kind — curated or
detected, now permanently, see D23 and D24 — streaming
integrations. `IAnimeListProvider` and `IAiRecommendationProvider` are the extension
points; nothing speculative gets built behind them. **No fake AniList integration.**

**Post-MVP** (brief §42, amended by D13 and D25): metadata enrichment beyond what a sync already
hands over, richer artwork keyed to TMDB/TVDB, genres and studios ·
optional AI providers, OpenAI-compatible endpoints, Ollama/LM Studio, scheduled re-ranking ·
MAL API sync · **write-back to AniList or MAL** · multi-user, authentication, household
profiles.

**The metadata line moved, and the distinction is deliberate.** *Enrichment* means going out to
fetch data AniQueue was not given — a separate call, a separate concern — and that stays
post-MVP. `duration`, `seasonYear` and `coverImage` arrive in the same response as `episodes`,
which Phase 5b already consumes, so declining them would mean discarding fields already in hand
to honour a boundary drawn before AniList was in the MVP. They are taken — and D25 has since
brought their *rendering* into the MVP as well, as Phase 9. `description` is declined
outright — it is read once and never filtered on, so the source
links already answer it.

**Genres and studios: deferred, with the shape decided so it is not re-litigated.** They are the
only catalogue data here that is many-to-many, so storing them usefully means normalised
`Genre`/`Studio` entities and join tables — a delimited or JSON column makes "has genre Shonen" a
`LIKE` scan, which §6's indexed-server-side-filtering requirement rules out at a few thousand
titles. That is Phase 3-shaped work, and there is no backfill penalty for waiting: because
`MediaListCollection` returns an entire list in one request, refetching to populate them later
costs a single call. Two details worth keeping: genre can be filtered but **not sorted**, being
multi-valued, while studio can be both because AniList's `studios` edges carry `isMain`.

**Genre and studio affinity is a stronger stretch goal than filtering.** "You rate Kyoto
Animation 8.4 and Shonen 6.1" computed from the user's own history is a local, transparent,
explainable ranking signal needing no model at all — which is exactly what Phase 7 says it
wants. It matters more than it looks, and D32 made it matter more still: with the decision
screen withdrawn, the AI score is the only thing that ranks a backlog, and nothing produces one
until the user has carried a scoring run. An affinity score is the only candidate that would
rank a backlog *without the user doing anything*, and it is the best argument for eventually
modelling genres at all.

**Hybrid ranking is a stretch goal, not a gap.** D14 left two meaningful orderings —
`QueueItem.Position`, which the user authors, and `RecommendationScore`, which a model
proposes — and a formula blending them was carried as `IRankingCalculator` until D32 removed
its only consumers. `RecommendationMode` and `ProfileSettings.DefaultRecommendationMode`
remain in the schema against the day somebody wants the blend. Reinstating it is a pure
function in Core and one more `LibrarySort` member, which is why it is safe to leave undone:
nothing has to be built now to keep it cheap later.

AniList *read* access is no longer here — D13 moved it into the MVP as Phase 5, because with
D11 and D12 it is the only remaining manual step in the loop. **Write-back stays post-MVP**
and should be approached carefully: it is the one direction that can damage a list the user
maintains elsewhere, and every safeguard in the import pipeline exists to protect data
flowing the other way.

### Artwork — measured here, built in Phase 9a and 9b

> **No longer a stretch goal** — see D25 for where each tier landed. Kept intact because
> everything below is measured, and the measurements are what those phases are costed against.
>
> **Two of those measurements have since been overtaken, and are kept rather than corrected.**
> D46 replaces the tier-3 dataset and remeasures its coverage; D47 finds that the tier-2 cover
> size chosen below is nine times larger than a thumbnail needs. Both are marked where they
> appear. The reasoning that produced them is still the reasoning those phases were costed
> against, and deleting it would leave the new numbers looking arbitrary.

**The premise is accepted as real rather than decorative.** A backlog of several hundred rows is
a wall of text, and recognising a show by its art is faster than reading its title — which makes
artwork a decision aid on the surface where decisions are made, not styling.

*This section used to add: "that is the same argument §7 makes for grouping franchises in the
backlog." D24 found that exactly backwards.* Art and collapsing solve the same problem, so they
are substitutes rather than allies — and making the wall scannable is the better of the two,
because it does not hide anything to do it. That reversal is the argument that ended grouping.

**Tier 1 — AniList already supplies more than we ask for, at no extra cost.** Measured against a
real 753-entry list:

| Field | Null | Note |
|---|---|---|
| `coverImage.extraLarge` | **0 of 753** | Same request cost as `large`; downscaling is possible, upscaling is not |
| `coverImage.color` | 59 (7.8%) | A dominant accent colour per title — themed cards with **no image loading at all** |
| `bannerImage` | 185 (24.6%) | But 95% of `TV` and 92% of `MOVIE` have one. The gap is almost entirely `OVA` (99) and `SPECIAL` (45) |

That last distribution is the design constraint: a banner-led layout works for exactly the
formats that dominate a watch decision, and needs a poster fallback for side content. Do not
build a layout that assumes a banner.

`coverImage.color` deserves particular attention — it is six bytes, present for 92% of titles,
and enables per-show theming without solving the image-serving problem below at all. It is the
highest value per byte on offer.

**Only `extraLarge` is taken in Phase 5b, and the rest waits deliberately.** Nothing renders any
of it yet, and the whole library refetches in one request, so storing fields no phase reads would
be the speculative infrastructure D11 argues against, for no saving.

*That choice was right about the timing and wrong about the size, and D47 corrects it.* The table
above compares availability, not weight: `extraLarge` is 83.3 KB against 9.7 KB for `medium`, so
filling a 40-pixel column with it costs 4.2 MB a page and 67 MB of cache. Once something actually
renders, the question stops being which field is most complete and becomes which is the right
size for the slot — and by then the URL lives on a row that can be refetched, so getting it wrong
is a job re-run rather than a migration.

**Tier 2 — serving it is the real work, and it is why `ICoverImageResolver` exists.** Rendering
AniList's URLs directly is hotlinking, and it fails in four separate ways:

- It is **someone else's bandwidth**, on a CDN that owes a third-party application nothing.
- **The URLs rot.** They carry a content hash — `bx16498-buvcRTBx4NSm.jpg` — so replacing a
  title's art changes its URL and every stored copy becomes a broken image.
- **One third-party request per card**, disclosing to AniList's CDN what the user is browsing.
  §9 already notes this audience skews toward Brave, whose Shields will block some of them —
  producing a page of broken images with no explanation.
- **Availability.** AniList down means no art anywhere.

Caching through AniQueue answers all four, and lands on a constraint that is already written
down: §6 forbids image binaries in the database, so the cache is the filesystem under `/data` —
which is exactly where §9's non-root bind-mount problem lives. Solve that once for the database
and it is solved for art too.

**Tier 3 — richer artwork needs an id mapping, and that mapping is now measured rather than
assumed.** Clearlogos, backdrops, character art and language-specific posters come from
fanart.tv, TMDB and TVDB, and **all three are TMDB/TVDB-keyed**. The cross-reference does not
have to be built: open datasets already publish it. `Fribb/anime-lists` merges AniList,
MyAnimeList, AniDB, Kitsu, TVDB and TMDB identifiers into one file, and `Anime-Lists/anime-lists`
is the long-standing AniDB↔TVDB source that carries episode offsets. The question is not
availability. It is coverage.

*Superseded by D46, which read the licence and found Fribb has none — the figures below are kept
because they are what tier 3 was costed against, and because the replacement is measured against
them.* Measured against the same 753-entry library using Fribb's merged dataset — 7.5 MB, 42,867
records, 20,687 of them carrying an AniList id:

| Format | Titles | In dataset | → TVDB | → TMDB |
|---|---|---|---|---|
| TV | 253 | 248 | **248 (98%)** | **248 (98%)** |
| MOVIE | 163 | 163 | 78 (48%) | 143 (88%) |
| OVA | 219 | 186 | 136 (62%) | 148 (68%) |
| SPECIAL | 73 | 72 | **11 (15%)** | **12 (16%)** |
| ONA | 31 | 31 | 18 (58%) | 18 (58%) |
| TV_SHORT | 13 | 13 | 8 (62%) | 8 (62%) |
| **All** | **753** | 714 (95%) | 500 (66%) | **578 (77%)** |

**Coverage tracks format, and that changes the cost of everything built on it.** The dataset
knows 95% of the library but keys only 77% to TMDB, and the shortfall is concentrated almost
entirely in `SPECIAL` and `OVA`. A mainstream TV-only library would sit near 98%; a library that
is 29% OVA and 10% special sits at 77%. Any estimate of this work has to be made against the
shape of the library, not a headline number.

Three consequences:

- **Tier 3 art is additive, not a replacement.** AniList covers 100% of covers and TMDB would
  cover 77%, so richer art layers over a base that is always present. Graceful degradation falls
  out of the data rather than needing to be designed, which lowers the risk of the whole tier.
- **Overseerr is in better shape than 77% suggests.** Requests concentrate on series and films —
  98% and 88% — because specials and OVAs are rarely individually requestable. The cheap half of
  §10's cost table lands on the well-mapped half of the library.
- **Both identifier types are needed, keyed by media kind.** Films are 88% TMDB but 48% TVDB;
  series are 98% on both. The data is typed accordingly — `"themoviedb_id": {"tv": 26209}` versus
  a `movie` variant — so a design assuming one external key is wrong.

**These identifiers do not fit `AnimeExternalId`, and that is D17's warning arriving in
practice.** The table assumes 1:1 identity; a TVDB or TMDB id is many-to-one and only meaningful
alongside the season it refers to, which the dataset supplies as `"season": {"tvdb": 1,
"tmdb": 1}`. Storing them as if they were peers of an AniList id would silently claim an identity
they do not have. They need the season carried with them.

**The fetch is the same shape as Phase 6's relation pass**, and should reuse its pattern rather
than invent one: a lazy batched pass over titles whose mapping is not yet resolved, rare, and
doing nothing in the steady state. **Cache the dataset under `/data` rather than vendoring it** —
7.5 MB re-committed on every refresh is permanent history in a public repository, it goes stale
for exactly the new titles a user is most likely to be planning, and `/data` is already where the
artwork cache lives. Every use is an enhancement, so a failed fetch must degrade silently.

*Confidence, stated plainly:* the AniList and coverage figures above are measured. **The
dataset's licence has not been read, and vendoring would be redistribution** — that must be
checked before either path is chosen. The fanart.tv, TMDB, TVDB and Kitsu characterisations are
from general knowledge; their current API terms, key requirements and rate limits need verifying
before any of them is committed to. Kitsu remains the exception worth remembering: an anime
database with 1:1 identity that publishes its own art, reachable through D17's table with no
TMDB mapping at all.

*The first of those was checked, and it decided the phase* (D46). Fribb carries no licence at
all, which removed vendoring as an option rather than permitting it, and `Kometa-Team/Anime-IDs`
takes its place on the strength of being MIT. The rest of that paragraph stands unchanged: the
API terms are still unverified and still gate 9b, and Kitsu is still the fallback nobody has
needed yet.

**One schema note, because it is the same shape as a decision already made.** More than one image
per title means `Anime.CoverImageUrl` stops being sufficient — poster, banner and later logo and
backdrop are a set, not a field. That is precisely the arity-1 denormalisation D17 has just
finished replacing for identity, and the answer is the same: an `AnimeImage` table keyed by kind
and source, not a column per image. Worth doing in one step if it is done at all.

*Done in one step, in 9a* (D47, §4) — and earlier than this note expected. The column did not
survive as far as the second image kind: it is written through a merge that preserves what is
already stored, so repointing it at a different size would have updated new titles and silently
left every existing one behind.

### Stretch goals — self-hosted neighbours

A self-hosted AniQueue very likely sits beside Plex, and often beside Overseerr. Both are
worth linking to, and neither should become an integration: **AniQueue decides what to
watch and hands off the how.** That keeps D11 intact — no data ownership moves.

These are stretch goals for after the MVP is complete, not commitments.

**The cost split matters more than the feature list.** Two very different things get
described with the same words:

| | Needs | Cost |
|---|---|---|
| *Search* link — `/search?query={title}` | A configured base URL | Trivial |
| *Precise* link, or an "on Plex" indicator | A Plex library sync, or an anime-ID → TMDB/TVDB mapping | Substantial |

Search links are worth doing on their own. An availability indicator is the expensive half,
and it is the half with the real product value — *"which of my planned shows can I start
tonight?"* — so it should be judged against the AniList API work rather than bundled with
the cheap links.

Specific things a future implementer will otherwise have to rediscover:

- **Overseerr is TMDB-keyed.** It knows nothing of AniList or MAL identifiers, so a precise
  request link needs an anime-ID → TMDB mapping (the community anime-lists datasets exist
  for this, at the cost of vendoring and refreshing them). A search link avoids the problem
  entirely.
- **Plex anime metadata is inconsistent.** Depending on the agent, items may carry AniList
  or MAL identifiers, only TVDB, or nothing but a title. Where identifiers are absent this
  becomes title matching, which is the same ambiguity that produces import conflicts and
  deserves the same rule: never apply a match the application cannot confidently identify.
- **Plex availability was considered as an LLM input and rejected.** Passing a library for
  the model to recommend from is affordable — a couple of thousand titles is perhaps 10k
  tokens — but it makes AniQueue a membership editor, which D11 rules out, and it discloses
  what media the user holds to an external service. As a *filter* it needs no model at all.
- **Base URLs come from user configuration and end up in an `href`.** Validate the scheme is
  `http` or `https` at the point the link is built. A `javascript:` base URL is stored XSS,
  and this is trivial to guard up front and awkward to retrofit.

The natural shape is one small provider — given an anime, return an optional URL and label —
with per-instance base URLs and an independent toggle each. MyAnimeList, AniList, Plex and
Overseerr all fit it, so the Phase 3 links below are the first implementation of the same
extension point rather than a one-off.

---

## 11. MVP acceptance criteria → phase

The brief's 25 criteria, mapped so completion is measurable.

| Criteria | Phase |
|---|---|
| 1–2 `docker compose up -d`, open in browser | 11 |
| 3–7 Upload MAL XML, preview, confirm, see statuses and scores | 2 |
| 8 Create/edit franchises | **declined — see D23** |
| 9 Collapse sequels into them | **declined in its stated form — see D24**; sequels are shown related and tagged on each row, and are one click from the queue |
| 10–12 Add to Up Next, drag to exact order, persist across restart | 4 + 11 — one click also queues a title and its unwatched sequels (D15, D24) |
| 13–14 Track progress, complete with a score | **declined — see D12** |
| 15 Filter backlog usefully | 3 |
| 16–22 AI request export, prompt, import, preview, apply, manual order intact | 7, automated by 8 |
| 23–24 Export full library as JSON, restore from it | **declined — see D33** |
| 25 Recreate container without losing the database | 11 |

**Criteria 13–14 are deliberately not met.** They ask AniQueue to record watch progress and
accept a score, which D12 declines: those belong to the service that already tracks them, and
a second copy here would drift within a day. Progress and scores are still *shown* — the
importer writes them — so criteria 6 and 7, seeing statuses and historical scores, are met.

**Criteria 8 and 9 join them, for a related reason.** D23 declines 8 outright: *"create/edit
franchises"* asks for an authoring surface, and AniQueue authors order and nothing else. D24
declines 9's stated form and answers what it wanted differently — sequels are visibly related,
labelled with what they are, and one click from the queue, but they are not collapsed into one
row. The roadmap's third problem statement is amended to match rather than left standing.

These are the places the brief and the built application deliberately part company, so they are
stated here rather than quietly reported as done. Between them, 13, 14 and 8 all decline the
same kind of thing: a surface on which the user maintains data some other service already
maintains, or that nobody should have to maintain at all.

**Criteria 23–24 join them, and D33 records why.** A JSON backup and restore of the whole
library is a second persistence format maintained alongside the schema, and the thing it
protects is already a single file: the SQLite database under `/data`, which Phase 11 must keep
intact across a container recreate anyway. Phase 7 still exports — but it exports what a
ranking needs, which is deliberately not a restore.

**And the decision screen is declined outright (D32)**, though the brief's §8 asked for one. It
is the only decline here that removes a surface rather than an obligation to maintain data, and
the reason is that both signals the brief named for it were removed by decisions taken since:
manual priority by D14, leaving the AI score as the only ranking input, which does not exist
until a scoring run has been applied. What remains of §8 is filters, and the backlog already
has every one of them.

---

## 12. Working agreements

- Integration branch is `development`. `main` is release-only.
- One feature branch per phase: `feature/phase-N-slug` → PR into `development`. A split phase
  gets one branch per part — `feature/phase-5a-slug` and so on — because the point of splitting
  it was reviewable PRs.
- Rebase onto `development` and resolve conflicts locally before opening a PR.
- No new third-party dependency without explicit approval. SortableJS is the only one
  pre-approved, and only for Phase 4.
- **Phase 6 needs no new dependency either.** Pacing the relation backfill is tested against
  `TimeProvider`, which is in the box, so nothing was needed to avoid sleeping in tests.
- **Phase 8 needs no new dependency either.** A chat-completions call is an HTTP POST with a JSON
  body, so `HttpClient` and `System.Text.Json` cover the courier as they covered AniList; D37's
  extraction uses `Utf8JsonReader`, which is the same package and is what makes it string-aware
  without hand-rolled brace counting; and the settings writer regenerates a document rather than
  round-tripping one, which is why no comment-preserving JSON library is needed (D36).
- **Phase 5 needs no new dependency**, which is worth recording because two were considered and
  both declined. YamlDotNet was declined by D20 in favour of the in-box JSON configuration
  provider; a GraphQL client library was declined because a GraphQL request is an HTTP POST with
  a JSON body, and `HttpClient` plus `System.Text.Json` are in the box.
- **LF line endings everywhere**, in the repository and in the working tree on every
  platform, enforced by `.gitattributes` (`* text=auto eol=lf`) with `.editorconfig`
  matching it. `.gitattributes` is the enforcement point rather than `core.autocrlf`
  because it is committed and so applies to every clone; `core.autocrlf` is per-machine
  and never travels. Batch files (`*.bat`, `*.cmd`) are the sole CRLF exception —
  `cmd.exe` can mis-parse LF-only batch files. Verify with
  `git ls-files | while read -r f; do tr -dc '\r' < "$f" | wc -c; done`.
- Amendments to this roadmap go through a PR that updates this file, so the decision record
  and the code move together.
- Comments explain **why**, not syntax. Decisions cite their `D`-number.

---

## 13. Local development workflow

- **The inner loop is `F5` on `AniQueue.Web`, not Docker.** Container debugging in
  Blazor Server adds a rebuild cycle per change for no diagnostic benefit. Docker is a
  Phase 11 deliverable and a pre-release gate, not the daily loop.
- **Do not accept Visual Studio's generated Dockerfile.** Container Tools writes a
  debug-oriented file tuned for its fast-mode volume mount. Phase 11 writes a production
  multi-stage one (SDK build → `aspnet` runtime, non-root, no SDK in the final layer).
- `AniQueue.Web` is the single startup project; no multi-project startup is needed.
- **EF tooling is a pinned local tool**, not a global install: `.config/dotnet-tools.json`
  fixes `dotnet-ef` at the same version as the EF packages. Run `dotnet tool restore` once
  after cloning. Using the CLI rather than the Package Manager Console means the same
  commands work in CI and in a container build.

  ```bash
  dotnet ef migrations add <Name> -p src/AniQueue.Infrastructure -s src/AniQueue.Web -o Persistence/Migrations
  ```

  `Microsoft.EntityFrameworkCore.Design` is referenced by **both** Infrastructure and Web —
  the tooling builds the model through the startup project's DI — with `PrivateAssets=all`
  so it never reaches the published output.

  **The scaffolder writes CRLF on Windows**, including the model snapshot it rewrites every
  time. `.gitattributes` normalises that on commit, so the repository stays clean, but the
  working tree does not — which contradicts §12's rule that LF applies in both. Strip it after
  scaffolding rather than letting three files drift per migration:

  ```bash
  git ls-files | while read -r f; do tr -dc '\r' < "$f" | wc -c | grep -qv '^0$' && tr -d '\r' < "$f" > "$f.tmp" && mv "$f.tmp" "$f"; done
  ```

- **The development database lands under the Web project, not the repository root.**
  `Database:Path` is `./data/aniqueue.db` in `appsettings.Development.json`, and a relative
  path resolves against the app's *content root*, so the file appears at
  `src/AniQueue.Web/data/aniqueue.db`. Both that directory and `*.db*` by extension are
  git-ignored, deliberately belt-and-braces: an imported library is personal data and must
  never reach the repository.
- Delete that `data` directory to start from an empty database; migrations and the default
  profile are recreated on the next run, and nothing else is — a first run is empty, and the
  way to get data into it is the way a user would (D27).
- **Sample data, when a surface needs rows to be looked at**, and never otherwise. Either the
  *http (sample data)* launch profile — which also points at its own `sample.db`, so it cannot
  land in the database a real account synced into — or:

  ```bash
  dotnet run --project src/AniQueue.Web -- --SeedSampleData=true
  ```

  Development only, refused if the library already holds anything, and it leaves AniList sync
  switched off in what it seeds. It covers the states a manual pass needs and an empty database
  cannot offer: a queue to reorder and empty, a relation graph to expand and walk, a hidden
  entry, a spread of scores, and an applied AI ranking. **Do not sync a real account into a
  seeded database** — the sample titles carry identifiers AniList does not issue, so the first
  real list that comes back without them reports them as missing, correctly (D19, D27).

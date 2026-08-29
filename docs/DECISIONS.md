# AniQueue — Decisions

Every architectural decision and deviation from the brief, numbered so it can be cited
from a pull request or another document. This is the record of *why*; the plan itself
is [`ROADMAP.md`](ROADMAP.md), and where the two disagree with the brief, both win.

A decision is amended by editing its entry in the same pull request that changes the
code. Reversals stay: an entry that lost is kept with the reason it lost, because the
argument that failed is the one most likely to be made again.

---

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
already the project baseline, so nothing is lost.

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
  uses what is already modelled — `Dropped` status; `IsHidden` was the other answer here, and
  Phase 18b deleted it — so nothing has to be written back to an external service.

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

*Amended by D55, which moved the expansion into the detail dialog and let it walk the same work
as far as it goes. "One edge out, never transitive" was a rule about a panel inside a table row,
and it survives where it is still true: only a direct neighbour carries a relation label, because
only a direct neighbour has an edge stating one. Everything else below stands — there are still no
groups, release order is still a fact rather than an opinion, and a set is still a property of the
title you asked about.*

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

*Half of what follows was deleted by Phase 18b, which removed hiding altogether: the per-row
hide toggle, the Hidden view in the status picker, `HiddenOnly`, the dimmed row and its badge,
and the status counts that had to exclude hidden entries. The rule this decision is actually
about — one action, on the row it acts on — is untouched, and the plus button is now the only
thing that rule governs on the backlog. The hiding paragraphs are left standing because the
argument they lost to is a different one (D11, applied where it had not been) rather than a
reversal of this.*

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
- **It seeded a hidden entry**, because the hidden view and its status-picker option only
  existed when something was hidden, and a surface reachable only after hiding a row by hand is
  one nobody checks. *Phase 18b deleted hiding, and that title went with it — it existed for a
  surface that no longer does.*

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
*Amended by D57, which moves the primary seat off this page and makes it a dropdown. The seat
stays single, which is what this entry was protecting; where it is chosen is what changed.*

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

*D57 replaces the radio with a single dropdown, and the argument above is why it can: the hazard
was two controls over one seat, not the control's shape. One dropdown on one page cannot express
two primaries or none, so it enforces what the radio pair had to be arranged across two cards to
enforce.*

Two smaller things follow from being able to see both cards at once. **Nothing is primary until
somebody chooses**: the entity defaulted the rank to zero, so every unconfigured source claimed
the seat and two claimed it simultaneously — and it disagreed with the import, which already
ranks an unconfigured source below a configured one (D29). And **the title language moved out of
AniList's settings** onto the page, because it is a profile preference rather than a fact about
a source; with a second card it would have been drawn twice, as two controls over one setting.

*Both are amended by D57. The empty seat is filled by defaulting to AniList, because a dropdown
offering "nothing" offers the tie back. The title language moves once more, off this page onto
the settings page, for the reason it left AniList's card: it describes how titles read, not where
they came from.*

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
protecting. A JSON round trip would additionally have to carry queue order,
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

*Amended by Phase 18a: the route is `/settings` and the page is titled Settings. Five tabs is what
fits across a phone, so the sixth destination had to be a place other settings can move to rather
than a page about one feature. What the page holds is unchanged — background work is still all
that is on it.*

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

**Five smaller rules, decided once.**

- **Immutable URLs, and a directory per kind.** Art is served from
  `/art/{kind}/{id}/{hash}{ext}` and cached at `<data>/art/{kind}/{id}-{hash}{ext}`, with a year's
  `max-age` and `immutable`. AniList's URLs carry a content hash, so replaced art changes URL,
  refetches, and arrives at the browser under a new address — no revalidation, and never a stale
  poster. The page already joins `AnimeImage` to know whether to render an image or a colour block,
  so the hash is a column on a join it is doing anyway.

  **The hash is what earns the year, so a readable filename cannot replace it.** Naming files after
  the title instead — `covers/1763-midnightpanther.png` — was considered and declined: the address
  would then be unchanged when the picture changed, so every browser holding a copy would serve
  stale art for up to a year with no way to push a correction, which is §10's measured "the URLs
  rot" moved from AniList's CDN onto ours. Two further reasons specific to this codebase. The
  displayed title is a *preference* (D22), recomputed library-wide when the language changes, and
  a native-script title yields an empty ASCII slug — so the name is neither stable nor always
  non-empty. And it would put a third party's string into a path, which is what the whitelist
  parser makes impossible today: traversal is not sanitised away, it is unrepresentable. A slug
  could be carried *alongside* the hash for readability, at the cost of a stored column; it buys a
  nicer `ls` in a directory whose entry point is the application.
- **A directory per kind, under one `art` root** — the readable half of that proposal, taken.
  Phase 9b turns 810 files into some four thousand across four kinds, and one directory holding
  all of them is worse to list, worse to sweep, and hides what a file is. The kinds sit under a
  single root so the volume gains one entry beside the database rather than four.
- **Disk wins.** The job's precondition is "row says cached *and* the file is there", so deleting
  the art directory to reclaim space heals within a tick instead of breaking every image
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

### D48 — Three art APIs were read, and none of them survives a self-hosted deployment

*Answers the question D46 left open and D47 deferred — whether TMDB, TheTVDB and fanart.tv may
actually be called — and the answer removes Phase 9b as written rather than adjusting it.*

**Every previous entry on this subject ended by saying the terms were unverified.** They have now
been read, and all three fail, for three unrelated reasons:

| Service | What it asks for | Why it does not work here |
|---|---|---|
| TMDB | A free key; personal, non-commercial use is permitted | *"Cache, for longer than 6 months, any information obtained through or from TMDB or the TMDB APIs"* is prohibited. It also mandates the TMDB logo and a verbatim disclaimer wherever its content appears |
| TheTVDB | A v4 project key; the free tier covers anything under $50k revenue | A free key is *user-supported*, which means every end user must hold a paid TheTVDB subscription and pass their own PIN to `/login`. The only alternative is embedding a project key in a published image |
| fanart.tv | A project key, with a personal key as an optional extra | The same embedded-credential problem, for one image kind TMDB already publishes |

**TMDB's caching clause is the one that actually decides it.** D47's model is fetch once, hash the
bytes, serve from an address that contains the hash, and give it a year's `max-age` with
`immutable` — the hash is what earns the year, and nothing ever re-checks. A six-month purge
obligation is the opposite instruction. Honouring it means TMDB rows carrying an expiry no other
row has, a sweep that deletes art currently on screen, and a `max-age` that has to be shorter than
a retention period the terms cap. That is not an addition to D47; it is a second, contradictory
lifecycle inside the same table, and it would have been discovered after the schema was built.

**TheTVDB's is the one that would have shipped broken.** The revenue tier reads as though a
self-hosted hobby project is exactly the intended case, and it is — for a project that
authenticates its *own* users. AniQueue is distributed as an image and run by strangers, so
"user-supported" means every one of them buys a TheTVDB subscription before a backdrop appears.
The alternative, a project key baked into a public container, is a credential in a published
artifact, which §6 does not survive. Neither is a bug to be worked around; the licence is
describing a different kind of application.

**One finding is recorded because it is what would tempt a return.** TMDB's
`/find/{external_id}` accepts `tvdb_id` as well as `imdb_id`. D46 assumed the MIT dataset's
missing TMDB column cost one extra request per film and left series reachable only through
TheTVDB; in fact the dataset's TVDB ids resolve to TMDB series directly, so series would have
reached TMDB at 97% rather than 47%, and TheTVDB's API would never have needed calling at all.
**The mapping was never the problem.** The plan was sound and the permission was not.

**What this deletes.** D46's dataset is retired without ever having been fetched — that entry
stays, because the licence reasoning is what stopped a worse mistake, not because anything now
reads it. `IIdMappingJob` leaves §5's table unbuilt. `ImageSource`'s allowlist stays at one host.
`ImageKind` keeps `Banner`, `ClearLogo` and `Backdrop`: the enum is stored as an integer and is
append-only, so removing them is a data-contract break in exchange for nothing, and an unwritten
member costs one arm of a switch. Their comments change from promising Phase 9b will fill them to
saying why nothing does.

**What survives is the only part the dialog needs**: a cover large enough to be a hero image.
AniList publishes `extraLarge` at 460px wide against the 100px `medium` 9a caches for a 40×60
slot, and D47 already priced it at 83.3 KB a title and 67 MB across this library. That measurement
was taken in order to *reject* `extraLarge` for a fifty-row page, where it costs 4.2 MB a page; a
dialog renders one image, so the number that disqualified it there does not apply here at all.

*18d put it on the fifty-row page after all, and the arithmetic is what changed.* The measurement
above was `extraLarge` at its published size; what the row now asks for is the same rendition
lazily loaded into a 64px slot, so a page fetches only the cards somebody scrolls to. The reason
for asking is that 64px on a three-times phone screen wants around 192px of image and the
thumbnail is 100 — visibly soft at exactly the element the phase promoted. This is served over a
LAN from a disk that already holds both.

*Both of those figures turned out to be low, and 9b measured the real ones* — 183.3 KB a title and
145 MB across the library, so a fifty-row page would have been 9.2 MB rather than 4.2. It
strengthens the argument for keeping `extraLarge` off the list and weakens the case that putting it
behind the dialog is cheap; §7's 9b entry carries the table and the open question.

**Two renditions therefore need a key, and `ImageKind` is the wrong place to put one.** Its own
definition is "what an image of a title actually shows", and a size is not what it shows; loading
sizes into it makes every future kind×size pair a member of an append-only contract that can never
be tidied. `AnimeImage` gains a `Rendition` column instead, and its unique index becomes
`(AnimeId, Kind, Source, Rendition)`. Two renditions have different bytes, therefore different
hashes, therefore different filenames, so they cannot collide wherever they sit — which is the
immutable-URL design paying for itself a second time.

**They still get separate directories, and the first attempt did not give them any.** Sharing one
was justified on exactly that no-collision argument, and it was the wrong argument: this entry's
own reason for a directory per kind is that *"one directory holding all of them is worse to list,
worse to sweep and hides what a file is"*, and 1,620 files in `art/posters` is that sentence
describing itself. **What it actually cost is the disk-wins rule.** D47 made "the file is there"
half the job's precondition so that deleting art reclaims space and heals within a tick — but that
property only exists at directory granularity, so with one directory the 145 MB of full-size covers
could only be freed by also blanking every list thumbnail until the job caught up. `art/thumbnails`
and `art/posters` restore it.

Side by side rather than nested. `posters/thumbnails` would have read more precisely, because one
name is a size and the other is a kind; it was declined for being a level deeper in service of
kinds nothing writes. The asymmetry is the price of the shallower tree, and it is only invisible
while `Poster` is the only kind — which D48 is the reason for.

**The pass gets longer and keeps its budget.** Two rows per title at 8.5× the bytes means a first
run no longer finishes inside `CoverArtJob`'s ten minutes, which was justified as room for a
library twice this one's size. It resumes correctly because progress is recorded per row, so the
only real consequence is that a fresh install shows the dialog a small poster for a while — the
same degradation `coverImage.color` was banked for. Raising the budget would hold a database
connection and a cancellation window open longer for no gain, so the number stays and the comment
that justified it is corrected.

*Confidence, stated plainly:* the three sets of terms were read in August 2026 and are quoted
above rather than characterised. They are the publishers' to change, so returning to any of these
services means reading them again rather than trusting this table. Nothing here was measured
against a running integration, because none was built.

### D49 — A dialog that sells a show reads differently from a list that filters one

*Reverses §10's outright decline of `description`, pulls genres and studios out of post-MVP, and
gives the artwork work a page one phase after it lands.*

*Amended by D55. The dialog is no longer only the case for one title: it holds the set the title
comes with, and where its score came from, because a phone-width row cannot hold either. It also
stopped being a renderer handed a record and reads its own data, which is what lets two pages
share both additions without either of them learning how.*

**D48 left 9b holding a bigger cover and nothing to put it on.** The surface that wants it is a
show detail dialog, opened from a backlog or Up Next row: genres, synopsis, studio, the large
poster, and the score, confidence and reason already sitting on `LibraryEntry`. Its purpose is
narrower than a detail page's, and that narrowness is what settles the field arguments below — it
exists to make an unwatched title look worth queueing.

**`description` was declined for a reader that is not this one.** §10's argument was that it "is
read once and never filtered on, so the source links already answer it" — true of a list row,
where a synopsis is a wall of text in a column with no room for it and a link to AniList costs one
click. It is not true of a dialog whose whole job is the pitch. The column has existed on `Anime`
since Phase 1 and has never once been written or read; this is what it was for.

**Genres and studios leave post-MVP on the same argument, and keep the shape §10 already chose.**
Normalised `Genre` and `Studio` entities with join tables, not a delimited column — §6 rules out a
`LIKE` scan for "has genre Shonen". Nothing in these two phases filters, so this is being built
ahead of its consumer; it is still the cheaper order, because it is one migration either way, both
are squashed into Phase 11's baseline, and the delimited version adds a data migration the day
filtering arrives. `Genre` is a table rather than an enum because AniList can add one, and an enum
member is a contract that cannot absorb that.

**The studio join carries `isMain`; the genre join carries nothing.** AniList returns animation
studios and producers in one edge list, and `isMain` is what separates "the studio" from the
companies that funded it. All edges are taken with the flag rather than filtered to the main one
in the query: it is the same query shape and the same migration, the marginal cost is a boolean
and a few thousand rows, and it leaves the studio-affinity signal §10 calls a stronger stretch
goal than filtering with its data already present. A title with no main studio flagged shows no
studio line rather than an arbitrary one — D25's silent degradation, applied to text. The two
joins therefore look deliberately unalike: one is a join entity, the other a pure join.

**None of it needs a backfill job, which is what makes this one phase rather than three.** The
sync's only list query fetches the entire `MediaListCollection` every time, so `genres`,
`studios`, `description` and `coverImage.extraLarge` are four fields added to one selection set,
arriving in one response, populating every existing title on the next scheduled sync. Phase 6b's
relations needed a paced backfill precisely because they are deliberately *not* on that query
(D24); these are.

**The synopsis is stored as AniList's own markdown, not as `asHtml`.** Three reasons, in the order
they matter. AniList descriptions carry spoilers wrapped in `~!...!~`, and a dialog written to
interest someone in an unwatched show must not print its twist — in the raw form that is a
delimiter anything can detect, while `asHtml` has already expanded it into markup that would have
to be parsed back out. Taking HTML would also hand a Blazor page third-party markup that only
`MarkupString` can render, which is unescaped injection from a source any AniList user can edit;
untrusted strings are encoded here, as the model's output already is. And storing the source's own
string rather than a transformation of it is D47's lesson repeated: the cover parser stored the
wrong rendition, the merge preserved it, and it could not be corrected without a migration. What
AniList said is what is stored, and every rendering decision stays a code change.

**The collections restate the merge invariant rather than inheriting it.** `Merge` reads "the
incoming value if it exists and is allowed to land", resting on the property that *a source never
erases a value by not carrying it* — a MyAnimeList export knows no episode duration, and reading
that silence as "there isn't one" would discard what AniList said. A set has no null, so the rule
has to be written again: **an empty incoming set is silence, and a non-empty one replaces.**
Without that, re-importing a MyAnimeList XML — which carries no genres at all — would strip the
genres off every title it identifies, in a way no build and no test would notice. Replacement
rather than union, because a union means AniList correcting a mis-tagged genre never propagates
and the set only ever grows.

**One consequence is named here so it is not filed as a bug later.** For a title both sources
identify, with MyAnimeList ranked first, `mayOverwrite` is false for AniList, so its synopsis and
genres are filled once and never updated again. That is not new — `EpisodeCount` and `ReleaseYear`
have always behaved that way (D18) — and the consistency is worth more than a special case for two
fields.

**Building it found the thing that would have made the whole phase inert, and it was in the
preview rather than the merge.** A preview item with no changes is `Unchanged`, and `CommitAsync`
skips an `Unchanged` item outright — so anything the *preview* cannot see is something the commit
will never write, however correct the merge is. Genres, studios, a synopsis and a full-size cover
are all invisible to a comparison written before they existed, so every title already in a library
would have looked unchanged and received none of them. The snapshot the preview compares against
therefore carries all four, plus a rendition flag each for the thumbnail and the full-size cover.
**The measured proof is the sync that followed: 810 updated, 0 unchanged**, on a library where
nothing but these fields had moved.

**Two smaller things the same run decided.** Which company is the *main* studio has to be compared
separately from which companies are credited, because a title recredited from Wit Studio to MAPPA
credits both either way — comparing the set alone calls that unchanged and never applies it. And
four collection `Include`s on one row multiply together in a single query, which EF warns about by
name; the title lookup is `AsSplitQuery` because eighty rows to build one entity is the warning
being right.

**A gap in 9a that this phase surfaced without fixing.** D47 says a changed `RemoteUrl` "clears
both failure states and re-fetches". The merge does exactly that, but it only runs on an item the
preview did not call `Unchanged` — and a cover URL that has merely rotated is *deliberately* not
reported, because reporting it would turn an idle sync into a library-wide list of updated rows.
So replaced art is picked up only when the title changed for some other reason. Left alone here
rather than widened into: the honest reading is that D47's sentence claims something stronger than
the code does, and which of the two should move is a question about churn that this phase has no
evidence to settle.

### D50 — A reply names the library it was built for

*Adds `Profile.LibraryKey`, one field to each side of the scoring interchange, and a refusal.
Changes nothing about how a title is identified.*

A real incident, and worth stating exactly because the visible half of it was the harmless half.
A development database was deleted, the library synced again from scratch, and a scoring reply
generated before the rebuild was pasted into the new one. Twenty-five of roughly two hundred and
fifty results named ids the new database does not have, and those were reported — a wall of
identical red sentences, one per result, which is what made the problem noticeable at all.

**The other two hundred and twenty-five matched.** `ScoringCandidate.Id` is the `Anime` row key,
and a row key from a deleted database names whatever the rebuilt one happens to have put at that
number. Nothing in the interchange establishes that those are the same titles. Whether they were
depends entirely on whether both syncs inserted in the same order, which is not a property
anything guarantees, tests, or could reasonably promise: one title added or removed upstream
between the two runs shifts every id after it. Had the rebuild produced a smaller id space with
no gap at the top, the reply would have applied a couple of hundred predicted scores to titles
that were never ranked, silently, with the preview showing plausible names throughout.

So the failure mode is not "an id names nothing". It is "an id names something else", and the
existing validation cannot see it, because it is not a fact about any individual result.

**AniList ids as the row key were proposed and declined.** The argument is a good one — external
ids are stable across a rebuild, AniList publishes `idMal`, and a MyAnimeList-only title could be
bridged to an AniList id with an `idMal_in` lookup. Four things sink it, and none of them are
about scoring:

- **It is not total.** A MyAnimeList-only row has no AniList id until something fetches one. D24
  has since removed user-created titles, so the *manual* half of this objection is dead, but the
  MyAnimeList half is not.
- **It is not ours to keep stable.** AniList merges and retires entries. The key is referenced by
  `LibraryEntry`, `QueueItem`, `AnimeImage`, `AnimeRelation` and `RecommendationRunItem`; a
  primary key a third party can invalidate is the worst kind to put in five foreign keys.
- **It re-couples identity to one service**, which is D17 run backwards. D17 declined typed
  per-platform columns as the arity-fixed denormalisation of a relation; making one platform the
  row key is that mistake with the general shape removed entirely.
- **The bridge it depends on has a cost in a different feature.** Giving MyAnimeList-only titles
  AniList identifiers removes what D19 calls protection that is "structural rather than
  configured": absence handling is scoped to rows carrying the syncing source's identifier, so
  eight hundred bridged titles against an AniList account holding fifty become seven hundred and
  fifty rows that "were listed and are not now".

**And it would not have helped anyway**, which is the argument that actually settles it.
`RecommendationRunItem` keys on `AnimeId` too, so every previous ranking — the whole basis for
comparing one run to the last — died with the database in the same instant. Making the pasted
reply portable rescues one artifact from a set that were all lost together. That is a restore
story, and D33 already declined restore: the database file is the backup.

**Decision:** a profile carries a `LibraryKey` — twelve hexadecimal characters, minted once when
the row is created and never changed. The request states it in the envelope, the prompt asks for it
back, and a reply that fails to name this database is refused whole, with one sentence, before any
id is matched. What "fails to name" means depends on how the reply arrived, and the section after
next is the argument about that.

Four properties this deliberately has:

- **No version bump.** The field is additive in both directions. Raising the version would refuse
  every reply a user is currently holding, which is precisely the harm this exists to report.
- **The key is not validated for shape.** Whatever it is, it either matches or it does not, and
  "does not" is already the answer.
- **The worked example in the prompt carries the request's own key, not a placeholder.** D37
  accepts the last object holding a `results` array, and that example is one — so a model copying
  the envelope out of the example rather than the request still names the right library.
- **It lives in the database, not in `userconfig.json`.** It has to be reborn exactly when the row
  space is. A key kept in configuration would survive the deletion it exists to detect — and the
  sample profile's separate configuration directory shows how easily configuration and database
  can come apart.

**A missing key was lenient for about an hour, and the reversal is the more interesting half of
this decision.** The original rule was that a reply naming *no* database is read exactly as
replies were read before: the parser tolerates a missing envelope on purpose, because models
return the array reliably and the wrapper unreliably, so requiring the key would refuse correct
rankings over a field that carries no ranking. Every clause of that is still true. It lost anyway,
on the population it was reasoning about.

The argument for leniency scoped the gap as *transitional* — replies written before the key
existed, a set that only shrinks. That is wrong twice over. The gap is permanent, because a model
that drops the envelope drops it in every future version too; and it belongs to every future user
of this application rather than to whoever is holding a stale reply this week. **If a user can do
a thing, eventually one of them does.** Against that population the costs are not close: refusing
a good reply costs one retry, and accepting a wrong one costs a library of scores that cannot be
told apart from correct ones afterwards. That is D31's own reasoning one level up, and D31 is
already the rule that nothing is applied in part for exactly this reason.

**So strictness is route-aware, and the routes differ structurally rather than by degree.**

- **Pasted** — a person carried the document, which is the only way a reply from the wrong
  database ever arrives. A missing or mismatched key is one error, and nothing is read.
- **Endpoint** — the request was built and the answer received inside one process. There is no
  document to mix up, and nothing a key could establish that the call stack does not. It is also
  the route that *cannot* supply one: `ScoringResponseSchema` deliberately declares no envelope,
  because requiring it on the wire made servers refuse replies AniQueue would have accepted, so a
  model constrained to that schema returns the results array and nothing around it. Requiring the
  key here would refuse every scheduled ranking.

A mismatched key is refused on both. The endpoint is not asked to name a database, but one that
names the wrong database is telling us something, and there is no reading of it that leaves the
reply safe.

`ScoringRoute` has **no default value**, and that is the point of it being a parameter rather than
a setting. A defaulted route is a silent answer to the only question in the method that decides
whether a wrong reply is refused, and every caller knows which it is.

**The refusal has to be actionable, or it is just a wall.** A pasted reply with no key is told
which line to add and the exact value to put in it. Copying a real value out of the request is
mechanical, and unlike a confirmation it produces evidence rather than an assertion.

**An explicit "this reply is for this library" confirmation was considered in place of the refusal
and declined, for two reasons.** It asks the user to assert precisely what only the request can
establish, and the honest expectation is that they would tick it every time — a checkbox between a
person and the thing they are trying to do is a checkbox that gets ticked. And it cannot even be
phrased: this codebase already uses "library" for the user's collection — the message beside it
says "there is no title 815 in your library" — so "is this reply for this library?" reads as a
question about their AniList account rather than about a file on disk. Every user-facing message
here says **"AniQueue database"** for that reason, while the wire field stays `library`, which is
what it is.

**What remains uncovered, stated plainly.** A pre-D50 reply carries no key. On the pasted route it
is now refused rather than half-applied, which is the right outcome but not a rescue: the reply
that caused this decision cannot be applied at all, and nothing can fix that retroactively because
the evidence was never written down.

**Unmatched ids and skipped titles are now summarised rather than listed.** Five are named, then
the rest are counted; a reply where *nothing* matches gets a single sentence saying so instead of
one error per result. The same cap covers the two skip warnings, which is where it was actually
needed: the reply that prompted all this produced twenty-five unmatched ids and twenty-four
"no longer waiting to be watched" warnings beneath them, and the second group buried the first.
This is presentation, not validation — an id naming nothing is exactly as fatal as it was, and a
skipped title is still only a warning — but a panel the user has to scroll past to reach the
button is a validation pass that has stopped communicating.

### D51 — One thing to carry, and one spelling for "everything"

*Amends Phase 7b's manual card and retires `HistorySize = 0`.*

Three small things the paste route got wrong, found by using it against a real backlog.

**The card offered four buttons where the answer was always the same two, in the same order.**
Copy the request, copy the instructions, download the request, rebuild. The instructions are
useless without the request and the request is unreadable without the instructions, so the first
decision on the page was a false one: *which of these do I need, and in what order do they go
into the box?* They are now one document — instructions first, then the request — offered twice,
as a copy and as a file. Instructions first because a model reads the whole message either way
and the person pasting it reads the top, and what they need to see is that this is a set of
instructions rather than a wall of JSON beginning nowhere.

The file is `.txt` rather than `.json`, because it is no longer only JSON: a `.json` file opening
with a paragraph of English lies about itself, and some upload boxes decide what they accept from
the extension. *Rebuild* is now *Regenerate*, which is what it does.

**Copy size is not a risk worth designing around, and this was checked rather than assumed.**
A 237-title request is about 103 KB. `navigator.clipboard.writeText` has no size limit worth the
name, the `execCommand` fallback is a textarea and has none either, and the download is already a
blob URL rather than a `data:` URI for exactly this reason. In the other direction the reply
arrives through a Blazor circuit, where the default 32 KB receive cap *would* have been a real
ceiling — `Program.cs` already raises it to `ScoringLimits.Default.MaxBytes`, 4 MB. What is left
is the destination's problem rather than AniQueue's: a chat box that refuses a long paste, or a
context window that will not hold it. The file exists for the first, and the size on the card for
the second.

**`HistorySize = 0` is retired, and an empty field now means all of them.** Zero meant "send no
history", producing a ranking that is a general opinion about anime rather than a prediction about
this person — which is the one thing this feature exists not to be. It was also what an empty
field produced, so "I have not set a limit" and "send no evidence of my taste" were the same
keystroke, while the two sizes beside it read an empty field as *everything*. One spelling for
"everything" across all three.

Null is now that spelling, which forces a distinction the settings store did not previously need:

- **Absent** — nobody has said. 200, the default.
- **Present and null** — somebody cleared it. All of them.
- **Present and unparseable** — a typo. 200, because falling back to *all* would turn a mistyped
  number into the largest request the page can build.

The first two cannot be collapsed. `EnsureExistsAsync` seeds `userconfig.json` from the settings
currently in effect rather than from `UserSettings.Defaults` — deliberately, so a first boot
cannot write an empty AniList account over one supplied through the environment — and on a first
boot that chain is empty. Reading an absent key as "all" would make a fresh installation write
null and then send every rated title it has. Presence is therefore asked of the configuration
rather than of the value, because the JSON provider keeps a key whose value is null and
`configuration[key]` cannot tell that from a key nobody wrote.

A stored `0` clamps to `1` rather than to null. It is the upgrade path for a file written when
zero meant something, and the direction matters: reading it as "all of them" would silently turn
the smallest request somebody chose into the largest one there is.

**Past rankings shows ten, down from twenty.** A scheduled sweep writes one run per batch rather
than one per sweep, so an evening against a real backlog fills the table with its own batches and
pushes out everything a person came to compare against. Ten is what fits beside the card above it.
Not paginated: this is an audit list read from the top, and paging controls for rows nobody
navigates to is scaffolding for a use that has not appeared.

Its two count columns are right-aligned in the header as well as the body. They were `numeric`
cells under plain headers, so each label sat over the far edge of the numbers it names.

**All three size fields now say the same thing when empty.** *Rankings to ask for back* read
"One for every title", which is the same fact as "All 237 of them" said a different way — and
three controls that all mean everything when left alone should not need three readings to
establish it. None of them accepts zero any more: it was never reachable on two of them, and the
third is what this decision retired.

### D52 — A candidate says what history says, and nothing more

*Removes `episodes` and `episodeMinutes` from the scoring request. Amends Phase 7's export
payload.*

Every candidate carried an episode count and an episode duration. The question that removed them
is the one nobody had asked: **does this field change a predicted score?**

- **A model that recognises the title already knows how long it is.** The ids and the title
  variants are what identify it, and they were already there.
- **A model that does not recognise it is told not to guess.** The prompt says "If you do not
  recognise a title, give it a low confidence rather than a guessed score" — so the case these
  fields were supposedly for is a case the design already answers differently.

`ScoringHistoryEntry` had reached the same conclusion from the other end and said so in its own
documentation: a history entry carries a title, a score, a media type and a year, because episode
counts "would treble the size of the largest part of the payload to say nothing about taste".
What was true of a title somebody has watched is true of one they have not.

**What it costs and what it buys.** Measured rather than estimated: removing them took a request
from 4,462 bytes to 4,077 over eight candidates, which is **48 bytes each**. Against the real
237-title request that is 11.4 KB of 103 KB — a tenth of the payload, near enough three thousand
tokens. On a frontier model that is nothing. On the self-hosted path it is not: §7 records a real
refusal at 13,782 tokens against an 8,192-token window, and this is the half of the payload a
candidate limit cannot shrink without also shrinking the question.

**What is left is exactly what history carries**, and that is the better reason than the size.
The two halves of the payload now describe titles the same way — id, names, media type, year — so
a model comparing a candidate against the ratings is comparing like with like rather than reading
richer rows on one side of the question.

**One thing is genuinely lost, and it is worth naming.** `episodeMinutes` was the only field
separating a twelve-episode series from a twelve-episode short; `mediaType` calls both of them
`Tv`. That is a real format distinction and a real taste signal. It is being given up because it
is one distinction at a tenth of the payload, and because nothing measured it — which is the
honest state of this decision. **The way to settle it is the method D43 used for `rank`:** run a
sweep with and without against the same library and compare the scores. If short-form titles move
in a way nothing else explains, `episodeMinutes` comes back on its own.

**No version bump.** The interchange version describes what a reply must satisfy, and a reply is
unaffected: nothing read these fields back, and a model that saw them in an older request is not
holding anything a new request contradicts.

### D53 — A page rendering a number is not a request

*Adds `IRecommendationService.MeasureAsync`. Amends Phase 7b's size estimate.*

The Recommendations page prints how large a run would be before anybody asks for one, and it
found that out by building two whole scoring requests: one of a single candidate for the fixed
cost, one of two so the difference gave the cost of a further title. Measuring rather than
estimating was right — a candidate carrying three title variants and two external identifiers is
several times the size of one written by hand — and the way it was done was not.

**It read and serialised every rated title twice to render one number.** On a real library that
is 563 history rows, twice, and the page initialises twice per visit because it prerenders and
then initialises again when its circuit connects. Four full history reads to show a figure nobody
had asked for. The method's own comment said it existed so that "building a whole request to
render a number would mean loading the backlog on every page view" — which it then did, twice.

**And both probes logged at the level a real request logs at**, so a server log on an idle page
filled with `Built a scoring request for profile 1: 1 candidates, 563 of 563 scored titles`. That
line is worth having when a ranking is about to happen and is noise otherwise, and nothing could
tell the two apart because nothing was told.

**Decision:** the measurement is its own operation. `MeasureAsync` returns the two numbers the
page needs — a baseline and a slope — plus the two counts it was reading the requests for anyway.
It builds **one** request of two candidates and takes the baseline by re-serialising that same
record with a shorter candidate list, which costs nothing because it is already in memory. One
database read per call rather than two, and the Information line now only ever describes a
request somebody is going to send.

`ScoringSizeEstimate.CharactersFor` carries the arithmetic, so the page multiplies rather than
re-deriving a formula the service already knows.

**A bug fell out of it.** The old probes never passed `IncludePersonalNotes`, while the request
they were predicting does. Somebody who had opted in was told their request was smaller than the
one they would actually send — on the one card whose number exists to warn about a model's
context limit. The measurement now takes the same options a request takes, and ignores only the
candidate limit, because it always probes with two and the caller multiplies.

### D54 — A list you act on stops being a table before it stops fitting

*Amends Phase 3's backlog listing and Phase 4's queue. Adds a narrow layout for both.*

*Superseded in its mechanism by 18d, and vindicated in its diagnosis.* The measurement below is
why the phase exists, and the rule in the title is the one 18d applied — it just applied it to the
table rather than to the table's stylesheet. Every rule this decision wrote is deleted: the
display:block cascade, the zero-height `::after`, the `order` on every cell, the data-labels, and
the cover column hidden at exactly the width where the picture turned out to be the most useful
thing on the row. The lists are grids of cards now, which need none of it.*

Measured at 375px before any of this existed: **the backlog was a 710px table in a 309px
window, and the queue a 499px one.** Both sat inside `.table-wrap`, which scrolls sideways, so
nothing was broken in the sense of overlapping or clipped. It was unusable in a way that is
worse than broken, because it looked fine.

- The backlog showed two icon buttons and a title squeezed to **98px**. Type, year, runtime,
  status, progress and both scores were off the right edge.
- The queue's five reorder controls began **42px past the edge** and ran to 532px in a 309px
  window. **The page whose entire job is ordering a queue could not order it on a phone**, and
  its only other route is a drag handle, which is the least reliable gesture on a touch screen.

**A table that scrolls sideways is the right answer for something read and the wrong answer for
something acted on.** The distinction is whether a control and the thing it acts on have to be
on screen together. The task list and the ranking preview are read — they keep the scroll. The
backlog and the queue are acted on, so below 720px they stop being tables: `display: block`
through the table parts, the header row hidden, and each row a wrapping flex line.

Three details that were not obvious:

- **The line break has to be an item of its own.** Flex wraps where a line runs out, so with
  nothing full-width the metadata crowds onto the title's line and the auto margin holding the
  actions at the right edge has no space left to push into. Making the *first field* full-width
  instead put that one field alone on a line. A row is a flex container, so its `::after` is a
  flex item: a zero-height item that fills the line is a line break spelled the only way this
  layout can spell one.
- **`:empty` does not match a cell holding whitespace**, and Razor renders whitespace. The
  spacer cell of an expansion row and an expander with nothing to expand were hidden by what they
  are rather than by being empty. *Both rules went with the expansions themselves in 18c (D55);
  the lesson is left because the next `:empty` selector written here will be wrong the same way.*
- **The narrow rules are more specific than the rule that hid the cover column**, so they put
  the thumbnail back. It is hidden again inside them. A `@media` block sets a width at which a
  rule applies, not a priority.

**What the wide layout keeps, and why the split is at the row rather than the page.** Above
720px nothing changes: the same twelve columns, the same header, the same alignment. Two
layouts for one list is a cost, and it is smaller than the alternatives — a column-hiding scheme
decides for the user which facts do not matter, and a separate mobile page is a second listing
to keep true.

**Ordering is by hand on a phone, not by drag.** The reorder buttons take a line of their own
below the metadata, which is also the reading order: this is the row, this is what it is, this
is where you can send it. Drag still works wherever a pointer does.

**Superseded by Phase 18, and it is worth saying which half.** The overflow this fixed stays
fixed — nothing scrolls sideways and the queue can be reordered. What it did not fix is that
the row still carried twelve fields, which is what running it on an actual phone made obvious.
Phase 18 empties the row rather than rearranging it, and replaces the table underneath.

**A browser made narrow is an approximation, so there is a way to open the real thing.** The
*http (lan)* launch profile binds to every interface rather than to `localhost`, which is the
only reason a phone on the same network cannot reach the ordinary one. The README carries it,
along with the inbound rule Windows wants and the two things that differ over plain http —
`navigator.clipboard` does not exist outside a secure context, and the device has to be on the
network rather than on mobile data. Development only, and it says so: no authentication and no
TLS, on a network the developer trusts.

---

### D55 — A title comes with a box set, and the dialog is where it lives

*Amends D24's "one edge out, never transitive" for this surface, reverses the placement half of
D26, and ends the split that kept the detail dialog free of services (D49).*

The backlog row carried two expandable panels. One listed a title's relatives, one edge out in
both directions; the other opened the model's reasoning for its score. Both worked, and neither
could survive 18d, which takes the row down to a poster, a title, one score and one action —
there is nowhere on a phone-width row to put a chevron that opens a paragraph.

**They move into the detail dialog, which already held most of what they said.** The dialog has
carried the AI score, its confidence and the model's reasoning since D49. What it did not carry
was *when* the score was decided, *how* it was carried and *which model* said it — three facts
that lived only in the row's panel, and which come across with it. Losing them quietly would have
been the easy version of this change and the wrong one: which model produced a score is the one
fact that tells somebody whether running it again is worth doing.

**A set is the same work, followed as far as it goes.** Prequel and sequel give the main run, and
side story hangs the specials off it. The walk follows those transitively and nothing else. A
spin-off is a separate work set in the same world, an alternative is a remake, and a summary or
compilation is the same story told again; none of them is in the box, and none of them is walked
*through* to reach anything else either, or excluding the edge would only hide one row of a
franchise it had already let in.

**`Parent` is not one of the four, and that omission is load-bearing.** It looks like the obvious
fifth — a special's own statement of what it belongs to — and it is the hole every spin-off climbs
through. AniList publishes `PARENT` as the counterpart of both `SIDE_STORY` and `SPIN_OFF`, which
this file already recorded under D24, so a parent edge cannot tell a special from a spin-off. It
also points the wrong way: a main work contains its side stories, and a side story does not
contain the work it branches from.

*Found by running it on a real library rather than by arguing about it.* Prisma Illya states
`PARENT` to Unlimited Blade Works, which states `SPIN_OFF` back; following the parent edge put
Fate/Zero — two further edges away, through a series Illya is not part of — into Illya's box set.
With the edge dropped, Illya's set is the nine Illya titles and nothing else.

**What that costs, stated rather than discovered.** A special whose *only* stored edge is its own
`PARENT` is not in any set. Ordinarily the work it belongs to states `SIDE_STORY` from its own
side and the edge is traversable from either end, so the special is found anyway. The row that is
lost is precisely the row that cannot be told from a spin-off.

**This reverses D24 for this surface, and D24's reasoning is what settles it.** "One edge out,
never transitive" was right for a panel wedged into a table row, where the question was *how is
this connected* and a whole franchise would have buried it. The dialog asks a different question
— *what am I taking on if I start here* — and season one is the answer to that from season three,
whether the graph puts one edge between them or three. **The one-edge rule survives where it is
still true**: only a direct neighbour carries a relation label, because only a direct neighbour
has an edge stating one. Anything further in shows no badge rather than a guessed one.

**Queueing the set replaces the sequel walk, and goes backwards.** *Queue this and what follows*
walked forward only, on the stated grounds that prequels are seasons already watched. They are not
always — an unwatched prequel is the single best reason not to start here — and status was doing
that work anyway: a Completed season is refused by the queue whichever direction it was reached
from. Direction was a proxy for a question the queue already answers, so it is gone, and the
button now offers the whole set in release order. Recaps and compilations are still skipped even
when the walk runs through them, which is unchanged and for D24's original reason.

**The action moved but did not change sides.** D26 put it beside the panel because queueing
several titles belongs next to the list of what they are. That still holds; the list moved, and
the button went with it.

**The dialog reads its own data now.** It was deliberately a renderer with no service of its own,
handed a record by whichever page opened it — a split that made sense while it only rendered. It
now has an action, and three things to load, and two pages open it. Both pages learning the same
four calls is the cost of keeping the old split, so the dialog takes the services and announces
`OnChanged` when it has queued something; the page re-reads.

**Up Next gains relations for the first time**, and gained them by deleting code rather than by
adding any: it shares the dialog, and it stopped handing it a record.

**Re-reading the whole page after the dialog acts is the opposite of what a row press does, and
deliberately.** D26 forbids re-querying on a row action because rows moving under the cursor lose
the reader's place. Nobody is reading the list while a modal covers it, and what comes back may
have queued six titles rather than one.

---

### D56 — `dev` is published continuously; a release still waits for the security pass

*Amends Phase 13's "CI builds the image and does not publish it" without reopening the gate it
was protecting.*

Phase 13 wrote one rule for publishing: nothing reaches Docker Hub until Phase 14 has run,
because an image on a registry is the moment a defect stops being local. That rule was written
against a single tag. There are three, and they do not have the same audience.

*A fourth was tried and withdrawn.* Each merge also pushed `dev-<sha>`, on the theory that
somebody bisecting would want it. Nobody did, and it doubled the tag list on Docker Hub to say
what the commit history already says.

| Tag | Written by | Who pulls it |
|---|---|---|
| `dev` | every merge into `development` | the author, on their own machine |
| `vX.Y.Z` and `latest` | a `vX.Y.Z` tag on a commit contained in `main` | a self-hoster's compose file |

**Decision:** `dev` publishes from today. `latest` and the version tag still wait for Phase 14,
and nothing automatic can produce them — a release requires somebody to push a tag, and the
workflow refuses one that is not already on `main`.

**Why publish anything at all this early.** The container is the deployment target and the inner
loop is `F5` (§13), so nothing exercises the container path unless CI does it on every merge. The
alternative is discovering at release time that the Dockerfile stopped working three phases ago,
which is exactly the class of failure this project keeps finding by running things rather than
building them.

**What it costs, stated plainly.** Phase 11's migration squash is free only while no database but
ours exists, and a published image is how somebody else's comes to exist. `dev` moves that
deadline from "the first release" to "the first time a `dev` image is deployed against a volume
anybody minds losing" — including the author's own. **So the squash is now a decision to take
before `dev` is deployed, not before `v1.0.0` is tagged.** It was taken in the same change, so the
migration folder holds one baseline and no database anywhere records a migration that no longer
exists.

**Registry credentials are `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN` in repository secrets**, and
appear in no committed file. The workflows log in only after the test step, so a red suite cannot
reach the registry at all.


### D57 — Preference lives on the settings page; a source card holds what a run reads

*Amends D30, which put the primary seat and the title language on the sources page, and amends
Phase 10, which planned a register of everything in `userconfig.json`.*

D36 drew the line between the two stores by **what a value describes**. This draws the same line
across the two pages, because the sources page had drifted into holding both: an account and a
conflict policy, which describe a source, sat in one disclosure beside a title language and a
primary seat, which describe how the whole library reads.

**Four preference columns existed and nothing read any of them.** `Theme`, `DateFormat`,
`DefaultQueueSize` and `DefaultRecommendationMode` were written by the initial migration and are
read by no page, no service and no job. Phase 10 was described as showing preferences that
already existed; what it would actually have been is building four features and calling them
settings.

**So one survives and three are dropped**, along with `RecommendationMode`, whose only reference
was the column. The backlog's default sort and filter columns Phase 10 also planned are not
added, for the same reason arriving one step earlier: the backlog already opens somewhere
sensible, and a stored default is a second answer to a question nothing was asking.

- **`Theme` stays** because the stylesheet was written for it — `app.css` already says that an
  explicit System/Light/Dark setting would set `data-theme` on `<html>` — and because a dark
  application that ignores a person who wants light is the one preference here somebody notices.
- **It is resolved during the server-side render**, in `App.razor`, which writes the attribute
  into the document the browser first receives. One row read per page load, no JavaScript, and
  no flash of the wrong theme. Reading it after the circuit connects would repaint in front of
  the user, which is the failure this setting exists to avoid.

*This is the first migration after Phase 11's squash, so it is the first one a `dev` image's
database will have to apply (D56). Dropping three unread columns is the cheapest possible thing
for that migration to be.*

**The title language moves once more.** D30 lifted it out of AniList's card onto the sources
page, because it is a profile preference rather than a fact about a source. That argument does
not stop at the page boundary, and the settings page now exists to receive it.

**Primary becomes a dropdown, and defaults to AniList.** D30's radio was chosen because two
per-source dropdowns could both say *Primary*; one dropdown, on one page, over one seat, cannot.
The default reverses D30's "nothing is primary until somebody chooses" deliberately: with a
dropdown, offering "none" is offering the tie back, and the tie — last import wins — is the
behaviour the setting exists to end. *A library where nothing was ever chosen changes behaviour
on upgrade, which is the cost, stated rather than discovered.*

**The seat still binds from a nullable option, and that is the second half of the upgrade
cost.** Every `userconfig.json` written while the seat could be empty holds
`"Sync:PrimarySource": ""`, and the configuration binder throws on an empty string for a
non-nullable enum — during startup, before anything serves, on exactly the installations that
have been running longest. `SyncOptions.PrimarySource` therefore stays `AnimeSource?` and its
two readers coalesce to `UserSettings.Defaults.SyncPrimarySource`, which is the one place the
default lives. There is still no "nobody": the type tolerates a file that predates the default,
and the page offers no way to write one.

**The per-source sync toggle is deleted, because it was never a second setting.** The sources
page's *Sync this source* and the settings page's power button on the sync task both write
`Sync:AniList:Enabled`. Two controls over one key is the bug D30 avoided on a text box and
acquired here on a switch.

**The register of `userconfig.json` is cut.** Phase 10 wanted one card naming every value in the
file and where it came from, to answer "which won". D36 removed the question: each value is
edited on the card that uses it, `appsettings.json` names no user-facing key, and a default is
not a layer — so the value on the card *is* the effective value, and a register would be a second
place showing the same numbers, which is the failure it was meant to prevent. What a person
needs when the pages cannot be reached is the file's path and whether it loaded, and both are
already said: on every card that saves to it, and in the banner when it breaks (D20).

**The cadence is renamed for what it drives.** *How often* becomes **Scheduled tasks** and *Check
for work* becomes **Frequency**. Not *Scheduled sync*: one cadence drives sync, relations, cover
art and scoring (D40), and naming it after one of the four would mislabel the other three.

**What is left on the sources page is what a run reads**: the AniList username, the MyAnimeList
file picker, the review of held changes, and the three settings that decide what an unattended
run may do — apply without asking, conflicts, and titles the source stopped listing. The
collapsed *Settings* disclosure goes with the rest; there is no longer enough behind it to be
worth hiding, and the username comes up into the card body where an unconfigured source already
offers it.

### D58 — Deleting everything is one action, and it keeps the profile

*Answers a gap rather than amending anything: AniQueue has never had a surface for managing its
own data.*

The only destructive control in the application deletes the relation graph, and it lives on the
sources page because that is where the coverage line explaining it lives. Everything else
accumulates with no way to clear it: a library imported from the wrong account, artwork for
titles long gone, a queue built against a backlog somebody no longer wants.

**Three rows, in a destructive section at the bottom of the settings page.** Delete all title
relationships, delete all artwork, and delete all. Each is a card row with a red button and a
dialog that says what is about to happen. The relations row is the existing control, moved.

*The button says what it does rather than showing a trash glyph.* The vendored sprite has no
trash symbol, and adding one would buy a picture beside a label that already reads "Delete
artwork" — where the icon buttons elsewhere on this page are icon-**only** and need a glyph to
mean anything at all. The relations control this row absorbs was a text button for the same
reason.

**It is called *Delete all*, not *clear the backlog*.** The backlog is a view of the library, so
emptying it means deleting titles — and a title takes its queue slot, its score and its pictures
with it. A name describing the page it was pressed from would understate every one of those.

**The profile row survives, and that is what "leaving the settings in place" means.**
`ProfileSettings` hangs off `Profile`, so deleting the profile would reset the theme and the
title language this action promises to keep, and the initializer would mint a replacement on the
next start. *The `LibraryKey` is not the reason.* A fresh key would be harmless here: every reply
carrying the old one names titles that no longer exist, and D50 already refuses such a reply
whole. Keeping the row is about the preferences on it.

**Run history goes with the library it describes.** A `JobRun` reading "changed 826" and a
`SyncRun` recording a successful fetch are both statements about rows that are gone.

**Sync is left switched on, and the dialog says what that means.** AniList titles come back on
the next run; the queue, the scores and any MyAnimeList-only titles do not. Somebody expecting a
clean slate and watching several hundred titles reappear would reasonably conclude the button
failed, so the sentence explaining it belongs in front of the click rather than after it.
Switching sync off as a side effect would be worse: this action is about data, and quietly
changing a setting is the thing D36 spent an entry making impossible.

**The artwork half needs no new code.** `CoverArtStore.RemoveUnclaimed` already deletes every
file no row claims; an empty claim set deletes the tree. `ArtworkService` refetches whatever is
missing from disk, so the cache heals itself afterwards without the rows being touched (D47).

**It is refused while any task is running.** Deleting the library from under a sync can throw
mid-run or re-add rows the moment it finishes, and a background job is the one thing on this page
that can be doing either while somebody presses a button. The control is disabled with the reason
said, rather than cancelling the running task first: cancellation is cooperative, so the wait is
the same and the second version has two failure modes instead of one.

**The dialog names counts; it does not ask for a typed word.** "Deletes 743 titles, 12 queued and
680 pictures" is a number somebody has to read, and reading it is the guard. A confirmation
phrase trains the habit of typing the phrase.

**None of this is a backup or an undo.** D33 already says the database file is the backup and the
copy is the operator's to keep, which is the same sentence the absence policy needs and for the
same reason.

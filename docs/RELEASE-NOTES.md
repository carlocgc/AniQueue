# Release notes

Changes that alter data or behaviour somebody would notice, newest first. This is not a
changelog of every commit — [`ROADMAP.md`](ROADMAP.md) holds the plan,
[`DECISIONS.md`](DECISIONS.md) the reasoning, and the git history holds the rest. What goes
here is the short list of things worth reading **before upgrading**, because a migration
will apply them without asking.

## v0.1.0 — the first release

**The first tagged image, and the first `latest`.** Until now the only published image was
`carlocgc/aniqueue:dev`, rebuilt from `development` on every merge and overwritten each time.
`carlocgc/aniqueue:latest` is what the compose file pulls, and `carlocgc/aniqueue:v0.1.0` is
the same image under a name that will not move. Nothing below this entry was ever in a
release, so there is nothing to upgrade *from* and nothing here to read before doing it.

**What it is.** Import a MyAnimeList export or sync a public AniList list, keep a hand-ordered
*Up Next* that empties itself as you watch, see what each title comes with, and rank the
backlog against your own past scores using a model you host. One container, one SQLite file
under `/data`, no account anywhere.

**What it is not.** It has had no external security audit — the pass that opened this gate was
a self-review against §6, and it found and fixed two real holes rather than none. It serves
plain HTTP. The optional password protects you from the other people on your own network and
from nobody else. **Do not put it on the internet**, and read the warning at the top of the
README before deciding what "on the internet" includes.

**Zero-dot-one on purpose.** The phases are built and the suite is green, but nobody else has
run this yet. Keep your own copy of `data/aniqueue.db`; it is the whole of the recovery path
and it is deliberately outside anything AniQueue can do to it.

## Phase 18b — hidden entries come back

**Anything you had hidden returns to the backlog and to the scoring candidate set.** The
`IsHidden` column is dropped, and with it the hide button, the *Hidden* view in the status
picker, and the exclusion that kept hidden titles out of rankings and out of a title's relations.
Nothing is deleted: an entry that was hidden is an ordinary entry again, with its status, score,
notes and history exactly as they were.

**Why it went.** Hiding was a second, local way to say *stop offering me this*, beside the one
AniQueue already trusts. Your lists live on AniList and MyAnimeList; AniQueue reads them and does
not author them. So the honest answer to "I do not want this ranked" is to take the title off the
list it came from and let the next sync agree — and that answer works everywhere, including in
the applications that own the list. A local flag only made AniQueue quietly disagree with the
source it reads.

**What to do about it.** If your backlog is suddenly longer than you expected, the titles that
came back are the ones you had set aside. Remove them from the list they came from — or change
their status there — and the next sync will take them out of AniQueue too. Downgrading restores
the column but not which entries were in it; that fact was only ever stored here.

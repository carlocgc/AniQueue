# Release notes

Changes that alter data or behaviour somebody would notice, newest first. This is not a
changelog of every commit — [`ROADMAP.md`](ROADMAP.md) holds the plan and the reasoning, and the
git history holds the rest. What goes here is the short list of things worth reading **before
upgrading**, because a migration will apply them without asking.

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

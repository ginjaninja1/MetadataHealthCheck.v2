Superseded Designs — MetadataHealthCheck.v2

Designs no longer live, kept only in case the current direction is later
found flawed and one of these needs revisiting. Do not build against
anything in this file unless that happens and a new decision explicitly
says so.

---

## Candidate Generation: Recording-Search-First (old Strategy B)

**Status: superseded 2026-07-12.** This is what §5.3 described, and what
`SoftBucketStrategy.cs` implemented, before the artist-search-first rewrite
(see Decisions.md). Superseded by a design review against real MusicBrainz
data.

**The old approach**: if neither Strategy A (own anchor) nor Strategy C
(borrowed anchor) was available, query
`recording:"{track}" AND release:"{album}"` (falling back to dropping the
`release:` clause if empty). Every distinct artist MBID the recording
search returned became a candidate, admitted unconditionally — no name/alias
gate before candidate generation, and `ArtistCandidateMinScore` was named in
the spec but never actually implemented anywhere.

**Why it was superseded**: relies entirely on downstream evidence (chiefly
negative name-similarity) to cancel out spurious candidates after the fact,
rather than never admitting them in the first place. A recording search
returns every artist who's ever recorded something matching the queried
track/album text, with no upfront plausibility check that any of them are
the artist actually being resolved. The current design (§5.3,
artist-search-first) closes this by checking name/alias plausibility
*before* a candidate is ever generated.

**What this approach might still be right for, if revisited**: cases where
MusicBrainz's real artist-credit text for the correct candidate doesn't
resemble the Emby-tagged name and isn't in MusicBrainz's own registered
alias list — a genuine MB data-completeness gap that could cause the
current design to miss a real candidate this one would have found. No real
example of this has been located and confirmed. If one is, that's the
concrete trigger for revisiting this.
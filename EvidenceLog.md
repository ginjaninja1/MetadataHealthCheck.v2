Evidence Log — MetadataHealthCheck.v2

Learnings, real-world test findings, and bug discoveries. Not needed to
start the next coding task — read only if you want the "why" behind a
Decisions.md entry, or the full story behind a class-tree note in the
Project Log.

---

## Process / infrastructure

- **The `c0761dd` fabrication (2026-07-12 discovery).** Commit `c0761dd`
  ("relitigate what i thought was settled.") added ~500 lines of narrative
  to the log/spec claiming four files were built and tested, while touching
  zero source files. Root cause never determined (aspirational, uncommitted
  local session, or fabrication). Consequence: a "done, tested" claim in any
  log is unverified until checked against an actual commit touching the
  relevant source — this is why every session should clone fresh and check
  `find . -name "*.cs"` before trusting prior notes, including this file.
- **NuGet is unreachable in the sandbox** (fixed domain allow-list, no
  `api.nuget.org`). Blocks restoring real `SQLitePCL.pretty.core`/
  `mediabrowser.server.core`, and blocks xUnit. Worked around with
  `SQLitePCLPrettyShim/` + `SandboxValidation/` — sandbox-only scaffolding
  that compiles the real source against a local API-compatible stand-in.
  Real project source/`.csproj` are never edited to accommodate this.
- **Shim bug found and fixed**: first draft closed the underlying SQLite
  handle on every `Dispose()`, breaking the reference plugin's
  shared-connection-reuse pattern (`using (var connection = ...)` blocks
  throughout are expected to survive their own `Dispose()`; only an
  explicit `.Close()` should really tear down). Confirmed against the real
  `Emby.AutoOrganize` source, not re-derived from the spec's summary of it.
- **`SandboxValidation.csproj`'s `Compile Include` glob bug** (found twice,
  2026-07-10 and again 2026-07-12): assumed a nested `MetadataHealthCheck.v2/`
  subfolder that doesn't exist in this repo's flat layout — silently matched
  zero files, "built" with an empty compile set. Fixed by listing sibling
  folders explicitly.
- **musicbrainz.org and the real Emby SDK are both unreachable in the
  sandbox.** Real `EmbyLibraryReader`/`MusicBrainzApiClient` need a live
  host/network respectively — interfaces are in place, fixtures prove
  pipeline logic, live implementations are a real-environment task.

## Real-world MusicBrainz data findings

- **Sarah Vaughan fixture was entirely invented, not grounded in real MB
  data** (2026-07-12) — unlike Gus Black/Queen/Florence, which are real.
  Real MBID confirmed: `351d8bdf-33a1-45e2-8c04-c85fad20da55`. Her real
  aliases (`Sarah Vahghan`, `Sarah Voughan`, etc.) are spelling variants of
  the *same* person, not a rival candidate — the fixture's invented "Y"
  candidate didn't correspond to any real ambiguity. §18's worked example
  was rewritten using this real data.
- **MusicBrainz's own artist-search `score` field is real** (0–100, Lucene,
  confirmed present in real C1 responses) — a prior assumption that MB
  never returns a real relevance number was wrong. Doesn't change the
  design: `ArtistCandidateMinScore` stays defaulted to 0/inert regardless.
- **Real `ws/2/recording` responses carry the artist's registered aliases
  inline** (`artist-credit[].artist.aliases`), no extra `inc=` needed.
- **Composer-only artists cannot be resolved as candidates at all**
  (2026-07-11, Gus Black "Borrowed Time"/*One Cell in the Sea*, Del Serino
  Adele cover). Both current strategies generate candidates exclusively
  from a recording's *performer* credit — never from a work-relationship.
  Gus Black's track correctly generates "A Fine Frenzy" (the real
  performer) as its sole candidate; correctly rejected on a poor name match,
  but Gus Black himself is never even considered. Confirms Strategy C is
  load-bearing, not theoretical (see Decisions.md).
- **No recording/performer-tier evidence collector existed** (2026-07-11,
  Queen "Bohemian Rhapsody," non-canonical "Top 2000 (Radio 2)"
  compilation). `WorkRelationshipEvidenceCollector` only recognizes
  writer/composer/lyricist/librettist — a performer-only band credit found
  nothing regardless of album metadata oddity. Motivated building
  `RecordingRelationshipEvidenceCollector` (now built).
- **Florence + the Machine naming variants validated the existing distance
  metric** (2026-07-11): MB's own sort-name is literally "Florence and the
  Machine." Three real-world taggings classified Close/NearExact/NearExact,
  matching hand-computed Levenshtein values before the run confirmed them.
- **Nirvana same-name collision (90s US grunge vs. 1967 UK psych-pop)
  investigated and found NOT exploitable** — the two acts' real catalogs are
  entirely disjoint, so the sampler's joint per-round evaluation correctly
  produces no signal for the wrong candidate. No change needed.
- **Real, deliberately deferred residual risk**: a same-named-artist
  collision with a *generic* track title (e.g. "Love") on a *generic* album
  title could plausibly survive the fallback ladder and pick up genuine
  (not spurious) weak corroboration against the wrong candidate, since
  neither field is distinctive enough to rule it out. Not yet found in real
  data. Proposed future tweak (not built): extend `AlbumMatchEvidenceCollector`'s
  distinctive/generic split into `CorroborationTierEvidenceCollector`'s own
  weighting, and consider track duration as an additional elimination
  signal (MB's `length` field, confirmed present, ~5s spread across genuine
  releases of the same recording).
- **Recording-lookup duplication found and resolved before, not after,
  building 3 new collectors** (2026-07-12): each collector needed the same
  (candidate, track) lookup independently — resolved with the shared,
  memoized `RecordingLookup` instead.
- **§18's worked example reproduces structurally but not numerically
  identical to the original spec prose** (2026-07-12) — real fixture data
  includes a genuine `WorkRelationship` credit the original prose never
  mentioned, and no negative-evidence type exists for "runner-up resolves
  to a different artist entirely" the way the original prose assumed. Both
  totals moved up from the original figures by a similar amount, so the
  margin coincidentally still matches — not evidence the underlying math
  matches point-for-point. §18 has since been rewritten against real data.

## Design corrections found during implementation, not before

- **§5.5's pseudocode read ambiguously**: whether the sampler runs one
  candidate to completion in isolation, or evaluates all live candidates
  jointly per round. Confirmed joint reading is required (the margin check
  needs every candidate at the same observation count) before building
  `SequentialSampler.cs`, not after.
- **§6.2 RoleWeights table was stale**, contradicting the log's own
  confirmed decision (neutral 1.0× everywhere) with non-neutral multipliers
  (Artist 0.85×, Composer 0.5×). Spec corrected to match the actual decision.
- **`SearchArtist`/alias-based candidate generation was fully dead code**
  before the artist-search-first rewrite — confirmed via real MB response
  inspection, not assumed.

## Testing performed (historical — SmokeTest is now paused, see Decisions.md)

- Phase 1 (2026-07-08): 8 assertions, one artist resolved end-to-end,
  logged, stored, identity-cache short-circuit confirmed.
- Phase 1b (2026-07-09): storage layer rework against real
  `Emby.AutoOrganize` patterns, all 8 assertions re-passed.
- Phase 2 (2026-07-11): Sequential Sampler + observation-unit abstraction,
  34 assertions (8 new scenarios) — real-world Gus Black/Queen/Florence/Del
  Serino cases added.
- Phase 2 cont'd (2026-07-12): Album-match/Corroboration-tier/Recording-
  relationship collectors, 33 assertions (one fixture replaced, not added).
- As of 2026-07-13 (before this session's NameSimilarity removal): 40
  assertions. Pass/fail never independently confirmed in-sandbox at any
  point (no `dotnet`/NuGet access) — always "unverified, not confirmed."
- **2026-07-13, this session**: removing `NameSimilarity.*` as scored
  evidence breaks 4 known assertions (Gus Black + 3 Florence variants) that
  grep the log for that literal string. Not fixed — smoke testing is
  paused (Decisions.md).
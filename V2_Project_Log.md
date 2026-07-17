# MetadataHealthCheck v2 — Project Log

This log is a **current-state snapshot**, not a narrative history. It exists
to answer three questions against the spec (`V2Specification.md`) and the
actual code in this repo, verified directly (not from memory of prior
sessions, not from a previous version of this log):

1. What has been delivered.
2. What is outstanding.
3. What is next.

Every entry below was checked against the actual file contents at the time
of writing (2026-07-15). Status labels used: **Complete** (matches spec,
no known gap), **Partial** (real, working code — genuine gap remains),
**Placeholder** (exists but is a stand-in, not the real implementation),
**Not built**, **Parked** (deliberately not implemented, by decision).

If you find an item below that doesn't match what you see in the repo, that
means this log itself has drifted again — flag it and we re-verify before
trusting either version.

---

## 1. Delivered

### Core model & interfaces (§4, §11.2)
- **Complete.** `Core/Model` and `Core/Interfaces` implement the data model
  and the entity-agnostic interfaces (`ISourceEntity`, `IObservationUnit`,
  `IObservationEvidenceCollector`, `IObservationUnitProvider`, etc.) as
  specified. None reference Emby/MusicBrainz/Artist/Album by name, per
  §11.4's extensibility requirement.

### Sequential Resolution Engine (§5.3)
- **Complete** for the phase-2 scope. `Core/Engine/ResolutionEngine.cs` and
  `SequentialSampler.cs` implement joint per-round evaluation of every live
  candidate (not per-candidate loops run to completion in isolation), the
  bucket-ordered stopping rule, and `BucketCeiling` as a budget rather than
  a target — matches §5.3's pseudocode.

### Scoring & Decision Gate (§5.4, §5.5, §6.4)
- **Complete** for the phase-2 scope. `Scoring/SimpleWeightedSumScorer.cs`
  and `ThresholdDecisionGate.cs` implement the three-way outcome against
  configured thresholds, including Tier-1-supersedes-album-match-precursor
  handling for the joint-feature case (§5.2).
- Bayesian scorer (the eventual replacement per §5.4) is **not built** —
  weighted-sum is the phase-1/2 stand-in, as the spec's Phased Build Plan
  (§19) allows.

### Candidate generation & confirmation (§5.1)
- **Complete** — `Resolvers/MusicBrainz/Strategies/SoftBucketStrategy.cs`
  implements the artist-search-first Stage 1 (admission gate, edit-distance,
  zero LLR) → Stage 2 (recording-level confirmation via the shared
  `RecordingLookup`) mechanism as specified.
- See Outstanding, below, for the real gap that remains adjacent to this:
  confirmation for composer-tier candidates.

### Evidence collectors (§6.1)
All four collectors now exist and are wired into `MusicBrainzArtistResolverPlugin.cs`:
- `AlbumMatchEvidenceCollector.cs` — **Complete**, with one documented
  limitation: only the first matching album per candidate is found, not
  all matching albums. Low-impact per its own in-file note.
- `CorroborationTierEvidenceCollector.cs` — **Complete.** Tier 1/2/3 +
  `MatchedViaAlias` → `NameMatchWeight`/`AliasMatchWeight` multiplier, per
  §6.3.
- `WorkRelationshipEvidenceCollector.cs` — **Partial.** Built and refactored
  onto the shared `RecordingLookup`, but inherits `RecordingLookup`'s gap
  (see Outstanding) — can only produce evidence for candidates that already
  have an artist-credit recording. Cannot help a composer-only candidate.
- `RecordingRelationshipEvidenceCollector.cs` (producer/arranger) —
  **Partial**, same `RecordingLookup` dependency and limitation as above.

### `RecordingLookup.cs` (§5.1, §5.2)
- **Partial.** The shared, memoized, three-rung fallback ladder
  (track+artist+album → track+album → track alone) is built and used by
  every evidence collector above plus `SoftBucketStrategy`. See Outstanding
  for the specific, confirmed gap.

### `ArtistNameNormalizer` (§5.1 Stage 1, §10.3 `NameNormalizationRules`)
- **Complete.** File is `ArtistnameNormalizer.cs` (lowercase "n" — cosmetic
  filename typo, harmless, low-priority rename candidate). Implements
  diacritic folding, regex-based rule table, case folding, whitespace
  collapse per the spec's Stage 1 normalization list.

### Emby source (§8)
- `EmbyArtist.cs` — **Complete.** Widened `EmbyTrackCredit` carrying
  AlbumArtists/Artists/Composers/Duration.
- `EmbyArtistProvider.cs` — **Complete**, including `ArtistFilter` support
  (§11.3).
- `EmbyArtistObservationUnitProvider.cs` — **Partial.** Implements 2 of the
  5 distance-seeking feed-order rules from §5.3.1 (different-album-first,
  different-title-first). The remaining 3 (single-credit-tracks-first,
  longer-titles-first, shorter-albums-first) are not implemented — likely
  need additional per-track/per-album fields not yet carried on the model.
- `IEmbyLibraryReader.cs` — interface only. A real `ILibraryManager`-backed
  implementation is **not built** — requires the live Emby SDK, which this
  sandbox cannot compile or test against regardless.

### MusicBrainz client (§7)
- `IMusicBrainzApiClient.cs` + fixture implementation — **Complete for
  current scope**, covering `SearchArtist` (with aliases inline, per C1),
  `SearchRecording` (with the fallback-ladder parameters, per C3/C4),
  `GetRelationships` (combined work-level + recording-level, per C5),
  `GetArtistDisplayName`, `GetArtistAliases`. A real HTTP implementation
  against live MusicBrainz is **not built** — needs network access this
  sandbox doesn't have to nuget.org/live APIs regardless.

### Storage (§9)
- `BaseSqliteRepository.cs`, `SqliteExtensions.cs` — **Complete**, ported
  from the `Emby.AutoOrganize` reference plugin's connection/WAL patterns.
- `MatchRepository.cs` — **Partial.** Implements 3 of the ~14 tables listed
  in §9.1: `resolution_candidates`, `evidence`, `match_results`. The
  remaining tables (`review_decisions`, `negative_cache`, `api_cache`,
  `identity_cache`, `artist_role_classification`, `artist_cooccurrence`,
  `anchor_dependencies`, `content_fingerprints`, `scoring_config_overrides`,
  `source_entities`, `scores`) are **not built**.
- `InMemoryIdentityCache.cs` — **Placeholder.** In-memory stand-in for the
  real `identity_cache` table, adequate for current phase-1/2 testing only.

### Diagnostics (§15)
- `StructuredLogger.cs` — **Partial.** Console-sink implementation only;
  the real `ILogger` sink (§15.1) needs the live Emby host and hasn't been
  wired.

### Fixtures
- `FixtureEmbyLibraryReader.cs`, `FixtureMusicBrainzApiClient.cs` —
  **Complete** for their purpose. Cover real-world cases used to validate
  design decisions: Sarah Vaughan/"Autumn
  Leaves", Gus Black, Del Serino, plus general naming-variant cases (Queen,
  Florence + the Machine).

---

## 2. Outstanding

Ordered roughly by how load-bearing the gap is, not by ease of fixing.

### A. Composer-tier confirmation has no working code path — the concrete blocker behind Gus Black / Del Serino
`RecordingLookup.cs`'s candidate-matching step filters every recording it
finds by `ArtistMbid == candidateMbid` **before** any relationship data is
consulted. This means it can only ever confirm a candidate who is already
credited as the recording's performing artist. A composer-only candidate —
who by definition is never that recording's artist-credit — has no code
path to get confirmed, even if Stage 1 admission somehow produced it as a
candidate. This is the same underlying problem the fixture set (Gus Black,
Del Serino) was built to surface, but the fix described in the current spec
(§5.1's relationship-scan Stage 2 variant: query track+album only, then
scan *all* returned recordings' relationship data for the candidate MBID,
rather than filtering by artist-credit first) is real, unbuilt design/build
work — not a rename or a config flag.

**Status: this is the confirmed next priority — see §3 below.**

### B. Data model / storage field rename: `GenerationStrategy` → `ConfirmationQueryShape`
The spec (§4, §9) names this field `ConfirmationQueryShape` now that there's
no labeled "strategy" concept — it should describe which Stage 2
confirmation variant (name-bearing vs. relationship-scan) produced a
candidate. The code has not caught up: `Candidate.GenerationStrategy`
(Core/Model) and the SQLite column
`resolution_candidates.generation_strategy` (`MatchRepository.cs`) both
still use the old name and the old "A"/"B"/"C" value scheme. Not a
contradiction requiring a decision — just a rename to carry through.

### C. `NameDistanceEvidenceCollector` is still registered as a scored evidence collector
Confirmed still wired in `MusicBrainzArtistResolverPlugin.cs`'s
`EvidenceCollectors` array, feeding `NameSimilarity.NearExact/Close/Poor`
LLR weights (still present in `ScoringConfig.cs`'s `EvidenceWeights`
dictionary) directly into scoring. Per §6.1, name/alias comparison is not a
standalone evidence type — it's a gateway (Stage 1 admission + Stage 2
multiplier), never a separate additive line. **Agreed framing**: the file's
Levenshtein/normalization logic is still needed and stays — it's what
Stage 1's admission-gate distance check and Stage 2's alias-match
determination actually run on. The fix is narrow: stop registering this
class as an `IEvidenceCollector` in the array above (so it no longer
contributes its own LLR line), while its comparison logic remains
reachable by Stage 1/Stage 2. The matching `NameSimilarity.*` entries in
`ScoringConfig.EvidenceWeights` should be removed at the same time — two
sides of the same open item, not two separate tasks.

### D. `MusicBrainzArtistResolverPlugin` constructor has an unused parameter
With `AnchoredRecordingStrategy` no longer in the active `Strategies` array
(see item E below), the `identityCache` constructor parameter is no longer
used anywhere in the constructor body.
Left in place deliberately rather than changed blind, since this repo
cannot be built/verified in the sandbox and a signature change risks
silently breaking composition-root wiring (§12.3) with no way to catch it
here. Small cleanup, not urgent.

### E. `AnchoredRecordingStrategy.cs` — now correctly parked, not wired
Not wired into the active `Strategies` array in
`MusicBrainzArtistResolverPlugin.cs` — anchoring is a parked concept in the
spec (§5.1/§10.1). The file itself is retained, not deleted, in case
anchoring is un-parked later. No further action needed unless/until that
decision is made.

### F. Storage: 11 of ~14 §9.1 tables not built
Listed under Delivered → Storage above. Not urgent — `resolution_candidates`,
`evidence`, `match_results` cover the current end-to-end scope; the rest
(identity cache persistence, negative cache, api cache, review decisions,
etc.) come with the phases that need them (§19 phases 3–4).

### G. Stale section-number references inside code comments
A comment in `MusicBrainzArtistResolverPlugin.cs` citing "§5.2 precursor"
for `AlbumMatchEvidenceCollector` references an old spec section number —
still correct in meaning, wrong number. Low-priority hygiene pass, not
urgent — flagged so it doesn't get mistaken for a real spec-vs-code
divergence later.

### H. A prototype evidence gathering and  scoring engine.
Onoging

### I. Smoke test apparatus
Being used to evaluate engine with sample observations
Onoging
---



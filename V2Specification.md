# MetadataHealthCheck v2 — Entity Resolution Engine
## Build Specification

## 1. Purpose & Scope

Build an entity resolution engine that maps Emby library entities to external
metadata identifiers, starting with **Emby Artist → MusicBrainz Artist**. The
engine is a generic framework: adding a second target system (e.g. Discogs)
or a second source entity type (e.g. Emby Album) must require no changes to
the core pipeline, only new implementations of existing interfaces.

**Priority order, in case of conflict:**
1. Accuracy of identification
2. Development clarity (logging, evidence traceability)
3. User clarity (explainable evidence and confidence, plain language)
4. Minimizing load on external APIs
5. Client/runtime performance

**Deployment constraint**: new engine (`MetadataHealthCheck.v2`), built
alongside the existing prototype. Must not modify, read from, or depend on
the prototype's data store. Own SQLite database, own configuration,
validated by comparison against the prototype before cutover (cutover
process not yet specified — see Phased Build Plan, §19, for current build
sequencing).

### Contents
1. Purpose & Scope — 2. Glossary — 3. Architecture Overview — 4. Data Model
— 5. Resolution Pipeline — 6. Evidence Catalog — 7. MusicBrainz Integration
— 8. Emby Integration — 9. Storage — 10. Configuration — 11. Class Structure
— 12. Task Invocation & Composition Root — 13. Config Pages — 14. Developer
Tools — 15. Logging — 16. Calibration — 17. Testing Strategy — 18. Worked
Example — 19. Phased Build Plan

## 2. Glossary

| Term | Meaning |
|---|---|
| **Candidate** | A possible MusicBrainz match for a source entity, prior to scoring |
| **Evidence** | One observed fact about a candidate, stored as a raw fact, not a pre-computed score |
| **LLR** | Log-likelihood ratio, natural-log (nats) units — summed to produce running confidence |
| **Corroboration Tier** | Tier 0 (asserted MBID in file tags), Tier 1 (Artist+Track+Album agree), Tier 2 (Artist+Track agree), Tier 3 (single field only) |
| **Processing Tier / Bucket / Role** | Which credit type a track observation came from: AlbumArtist, Artist, or Composer — also determines processing order |
| **Anchor** | *Parked concept* — a confirmed MBID for another artist on the same observation, which could in principle be used to constrain a MusicBrainz recording query. Not implemented or used by any pipeline logic in this document (§5.1, §10.1) |
| **Track Observation Feeder** | Selects/orders which Emby track to observe next — no candidate/LLR knowledge (§5.3.1) |
| **Sequential Resolution Engine** | The adaptive, one-observation-at-a-time evidence loop that stops as soon as cumulative confidence crosses an accept/reject bound (§5.3) |
| **Identity Cache** | Store of already-confirmed source→target mappings, checked before any new resolution work |
| **Negative Cache** | Store of confirmed non-matches, to avoid re-querying dead ends |

---

## 3. Architecture Overview

### 3.1 Three Decoupled Axes

| Axis | Phase 1 value | Future values |
|---|---|---|
| Source entity type | Emby Artist | Emby Album, Emby Track |
| Target system | MusicBrainz | Discogs, TheAudioDB |
| Target entity type | MusicBrainz Artist | MusicBrainz Release-Group, Recording |

A resolution task is always `(SourceEntity, TargetSystem, TargetEntityType) → MatchResult`.
No component may assume a specific combination.

### 3.2 Pipeline

```
════════════════════════════════════════════════════════════════════
 LIBRARY SYNC (scheduled task — runs once per invocation, not per-artist)
════════════════════════════════════════════════════════════════════
  Lightweight Tier Classification Pass — library-wide calls, name/id/
  role only, no track-level hydration (§8.2, calls E1b/E1c)
        │
        ▼
  Persist/refresh: artist_role_classification (§9), tagged source=provisional
  — used only to fix wavefront processing order; not authoritative and never
  used for scoring
        │
        ▼
  Assign every artist a provisional tier: highest role = AlbumArtist >
  Artist > Composer


════════════════════════════════════════════════════════════════════
 TIERED WAVEFRONT — process AlbumArtist tier, then Artist, then Composer
════════════════════════════════════════════════════════════════════
  For each artist in the current tier, strictly one at a time (no concurrency, §12.1):
        │
        ▼
  Identity Cache check — already resolved and uncontradicted? Reuse, skip below.
        │
        ▼
  Negative Cache check — known dead end, unchanged? Skip.
        │
        ▼
  ┌───────────────────────────────────────────────────────────────┐
  │  PER-ENTITY RESOLUTION (§5)                                     │
  │                                                                  │
  │  1. Per-Artist Track Read (§8.2, call E2) — full, fresh read of   │
  │     this artist's tracks, done now, not upfront. 
  │  
  │  2. Artist Candidate generation (§5.1)                           │
  │  3. Sequential Resolution Engine (§5.3) — one observation at a    │
  │     time, stop as soon as accept/reject bound is crossed.         │
  │  4. Decision gate (§5.5) — auto-accept / auto-reject /            │
  │     needs-review.                                                 │
  └───────────────────────────────────────────────────────────────┘
        │
        ├── auto-accept ──► identity_cache + match_results
        │                   
        ├── auto-reject ──► negative_cache
        └── needs-review ─► review queue ──(human decision)──┐
                                                                │
        ┌───────────────────────────────────────────────────────┘
        ▼
  ACTIVE LEARNING FEEDBACK (§5.7)
     - permanent override recorded immediately
     - periodic: recompute LLR weights from accumulated labels
     - periodic: calibration backtest (§16) — histogram confidence buckets
       against known outcomes; replay alternate strategies against
       cached evidence to check under/over-sampling
     - if an anchor is overturned → cascade re-open via anchor_dependencies
       (parked mechanism, §5.1/§10.1 — not currently active)
```

## 4. Data Model (indicative not final, current codebase is the source of truth)

```csharp
namespace MetadataHealthCheck.v2.Core.Model
{
    public interface ISourceEntity
    {
        string SourceSystem { get; }   // "Emby"
        string EntityType { get; }     // "Artist" (Phase 1); "Album" later
        string SourceId { get; }       // Emby ItemId
        string DisplayName { get; }
    }

    public class Candidate
    {
        public string TargetSystem { get; set; }          // "MusicBrainz"
        public string TargetEntityType { get; set; }      // "Artist"
        public string TargetId { get; set; }               // MBID
        public string ConfirmationQueryShape { get; set; }  // "name-bearing" | "relationship-scan" — which Stage 2 variant (§5.1) produced/confirmed this candidate
        public string GenerationQuery { get; set; }         // literal query string, for logging
        public DateTime CreatedAt { get; set; }
    }

    public class EvidenceRecord
    {
        public string CandidateId { get; set; }
        public string EvidenceType { get; set; }           // key into the evidence catalog, §6
        public string RawValue { get; set; }                // the observed fact — NEVER a pre-computed LLR (required for the re-score operation, §14.2)
        public string Role { get; set; }                    // AlbumArtist | Artist | Composer | null
        public string SourceTrackId { get; set; }
        public string AlbumId { get; set; }                  // for corroboration-tier supersession, §6.3
        public string RelationshipType { get; set; }          // writer|producer|arranger|... , null if not applicable, §7.2
        public bool MatchedViaAlias { get; set; }              // §5.1 Stage 2 / §6.3 — drives NameMatchWeight/AliasMatchWeight
        public string Rationale { get; set; }                  // human-readable sentence — always populated, §5.4
    }

    public class ScoredCandidate
    {
        public Candidate Candidate { get; set; }
        public double RunningLlr { get; set; }               // nats
        public double Confidence => 1.0 / (1.0 + Math.Exp(-RunningLlr));
        public IReadOnlyList<EvidenceRecord> EvidenceSoFar { get; set; }
    }

    public class MatchResult
    {
        public string SourceSystem { get; set; }
        public string SourceId { get; set; }
        public string TargetSystem { get; set; }
        public string TargetEntityType { get; set; }
        public string TargetId { get; set; }
        public string Status { get; set; }                    // auto_accept | auto_reject | needs_review | human_confirmed | human_rejected
        public double Confidence { get; set; }
        public double Margin { get; set; }                     // over runner-up candidate
        public string ScoringConfigVersion { get; set; }        // traceability — which weights produced this decision
        public DateTime DecidedAt { get; set; }
        public string DecidedBy { get; set; }                    // "system" | Emby user id
    }

    public class ResolutionContext
    {
        public EngineConfig EngineConfig { get; set; }
        public ScoringConfig ScoringConfig { get; set; }
        public DeveloperConfig DeveloperConfig { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public IProgress<double> Progress { get; set; }
        public string RunId { get; set; }                        // groups all log lines/evidence from one sync run
    }
}
```

## 5. Resolution Pipeline

### 5.1 Candidate Generation and Confirmation

Candidate generation is a single mechanism in two stages — there is no
separate generation strategy for any bucket (AlbumArtist/Artist/Composer).
Stage 1 admits candidates by name; Stage 2 confirms each admitted candidate
against the artist's own observed tracks. Both stages apply identically
regardless of whether the Emby entity being resolved is an album artist, a
performing artist, or a composer — MusicBrainz treats all of these as
artists, retrievable via the same `/artist` endpoint.

**Stage 1 — Admission gate** (candidate generation, one-time per artist,
contributes zero evidence). Query `artist:"{source artist name}"` (§7.2,
call C1) to get a pool of candidate artists **and their registered
aliases** in one call. Normalize both the Emby-tagged name and each
candidate's primary name/every alias using a configurable replacement table
(§10.3 `NameNormalizationRules`: strip leading "The", fold `&`/`and`/`+`,
strip apostrophes, strip `feat`/`featuring`/`vs`/`with` credit suffixes,
strip punctuation, fold case, collapse whitespace, normalize diacritics).
Compute Levenshtein distance; name and every alias weighted **equally** —
the question is only "does this MBID deserve a `/recording` lookup at all."
Admit if the best distance across name-or-any-alias clears
`ScoringConfig.CandidateGeneration.ArtistCandidateMaxEditDistance`.
**Contributes nothing to the LLR sum.**

`ScoringConfig.CandidateGeneration.ArtistCandidateMinScore` (MusicBrainz's
own C1 relevance `score`, 0–100) is a distinct, separate gate, defaulted to
**0** (inert until real tuning data exists).

**Stage 2 — Recording-level confirmation** (per track, repeatable, real
evidence weight). For each admitted candidate, search MusicBrainz
recordings from the artist's own observed tracks via the shared per-track
recording lookup (`RecordingLookup`, §5.3), and check whether the
candidate's MBID appears in the response. This is the same lookup-and-
confirm step for every bucket — the only thing that varies is the query
fields used and the part of the response searched:

- **AlbumArtist/Artist-tier observations**: the candidate's own name is a
  valid recording-search field. Query trackname+artistname+albumname,
  falling back to trackname+albumname, falling back to trackname alone,
  until a hit is found. Confirmation is expected in the artist-credit field
  of the response.
- **Composer-tier observations**: the candidate is not the recording's
  performing artist, so their name cannot be used to constrain the search.
  Query trackname+albumname, falling back to trackname alone. Confirmation
  is sought by scanning the recording response's relationship data
  (work-rels/work-level-rels for composer/writer credits, recording-level
  artist-rels for producer/arranger credits) for the candidate's MBID,
  rather than the artist-credit field.

Whether a candidate is a composer, an album artist, or a performer,
generation and confirmation both work the same way in the end: find a
candidate MBID inside a recording response. Only the query shape and the
field searched change with bucket.

Each recording lookup rung risks its own false-positive shape; accepted as
a known, low-probability-in-practice risk, relying on the shared
name-match evaluator's rejection of poor matches as the real safety net
(§5.2). Every lookup records which rung produced its result
(`RecordingLookupRung`, diagnostic-only for now). Implemented once, shared,
and memoized per (candidate, track) pair
(`Resolvers/MusicBrainz/Evidence/RecordingLookup.cs`).

**Known accepted trade-off**: admitting candidates by name/alias match
first risks *excluding* a correct candidate upfront if its real
MusicBrainz artist-credit text doesn't resemble the Emby-tagged name and
isn't in MB's own registered alias list (a genuine MB data-completeness
gap). The Stage 1 threshold is deliberately loose — a marginal name match
can still be admitted and let Stage 2's real track evidence decide it.

**Unconfirmed — to be validated against real cases (e.g. Gus Black, Del
Serino) once the pipeline is running, not treated as settled in advance:**
- Whether Composer-tier confirmation should check *only* the relationship
  fields expected for that bucket, or always check both artist-credit and
  relationship fields regardless of bucket (a track's participants can
  genuinely appear in both).
- The exact fallback rung order for Composer-tier lookups —
  trackname+albumname then trackname-alone is the working assumption.

**Parked, out of scope for now**: constraining a recording lookup using
another, already-confirmed artist on the same observation as an "anchor"
(e.g. using a confirmed "Adele" to help resolve a composer-only "Gus Black"
credited on the same track) is a plausible future refinement, retained in
the data model and config (§9 `anchor_dependencies`, §10.1 `Anchoring`) but
not implemented or used by any pipeline logic described in this document.
Revisit once Stage 1/Stage 2 above are proven out.

### 5.2 Evidence Collection Rules

- Every static evidence collector implements `IEvidenceCollector<TSourceEntity>`
  (§11.2) and returns an `EvidenceRecord` with the **raw observed fact**,
  never a pre-baked LLR — the LLR is looked up from `ScoringConfig` (§10.3)
  at scoring time. Required for the "re-score without re-fetching" developer
  operation (§14.2).
- **Name/alias comparison is not itself an evidence type — it is a
  two-stage gateway spanning candidate generation and corroboration
  weighting.** Stage 1 (§5.1) is a one-time, per-artist, binary pass/fail
  with zero LLR contribution. Stage 2 (§5.1) applies per recording-lookup
  result, as a multiplier (`NameMatchWeight`/`AliasMatchWeight`, §10.3) on
  whatever Corroboration Tier value (§6.3) that lookup would otherwise
  contribute — never a separate additive line. Two candidates with
  genuinely identical track-level evidence and identical match-type on
  every track are a correct needs-review tie.
- Genuinely diverse per-observation evidence (different album, different
  track) is independent and counted at full weight — no decay factor for
  repeated observations.
- **Joint-feature case**: a full Tier 1 triple for a given album supersedes
  the standalone album-match contribution for that same album (see the
  Album match rows, §6.1) — resolved at scoring time in
  `SimpleWeightedSumScorer`, not the general `JointEvidencePairs` mechanism
  (`JointEvidenceRules.cs`, unbuilt for any other case).
- **`MatchedViaAlias` determination**: the recording lookup's fallback
  ladder (§5.1) is evaluated by a single shared name-match evaluator, which
  determines, per recording result, whether the match was via the
  candidate's primary name or only via a registered alias
  (`EvidenceRecord.MatchedViaAlias`). Every lookup also records which rung
  of the ladder produced its result (`RecordingLookupRung`,
  diagnostic-only for now).
- MusicBrainz's search relevance `score` field (§7.2, calls C1/C3/C4) is
  real (0–100, Lucene) but **never** fed into the LLR sum, and not currently
  used for the Stage 1 admission gate (that's edit-distance based).
  Retained as `ScoringConfig.CandidateGeneration.ArtistCandidateMinScore`,
  default 0.
- Composer-bucket credits must be decomposed by actual MusicBrainz
  relationship type, not treated as one undifferentiated fact. Requires the
  recording lookup to request **both** work-level and recording-level
  relationships in one call: `inc=artist-credits+artist-rels+work-rels+work-level-rels`
  (§7.2, call C5). Work-level (`work-rels`+`work-level-rels`) surfaces
  composer/lyricist/librettist/writer; recording-level (`artist-rels`)
  surfaces producer/engineer/arranger (arranger is recording-level by MB
  convention). Record the specific relationship type verbatim in
  `EvidenceRecord.RelationshipType`.

### 5.3 Track Observation Feeder and Sequential Resolution Engine

The **Track Observation Feeder** (§5.3.1) is a pre-engine step that selects
and orders which Emby track to observe next. The **Sequential Resolution
Engine** (below) takes each fed observation, generates/confirms candidates
against it, accumulates evidence, and decides when to stop. The feeder knows
nothing about candidates or LLR; the engine knows nothing about track
selection. Each observation carries its bucket tier (AlbumArtist/Artist/
Composer), which affects the role-weight multiplier (§6.2) and which
confirmation query/field-search variant Stage 2 uses (§5.1) — Composer-tier
observations use the relationship-scan variant rather than a separate
generation strategy.

The stopping rule and the sampling budget are the same mechanism.

```
running_llr = 0
for bucket in [AlbumArtist, Artist, Composer]:          # highest signal first
    for obs in feed_from_bucket(bucket):                 # ordered by the Feeder, §5.3.1
        running_llr += evidence_llr(obs) × role_weight(bucket)   # §6.2
        if running_llr >= AutoAcceptThreshold:
            STOP → auto-accept                            # may fire after a single observation
        if running_llr <= AutoRejectThreshold:
            STOP → auto-reject
        if observations_taken_in(bucket) >= BucketCeiling[bucket]:
            break                                          # exhausted this bucket's budget — escalate to next bucket
if no bound was crossed after all buckets/budgets exhausted:
    STOP → needs-review
```

`BucketCeiling` (default: AlbumArtist 3, Artist 4, Composer 6) is a sampling
**budget**, not a target — the actual stop can occur anywhere from the first
observation up to the ceiling.

**Important**: the pseudocode above is written per-candidate for
readability, but the engine evaluates every live candidate **jointly, one
round at a time** — not one candidate's loop run to completion in isolation.
This is required by §5.5's margin check, which compares the top candidate's
cumulative LLR against the runner-up's: that comparison is only meaningful
if every live candidate has had the same observations at the same point.
Concretely: draw one observation, score it against *every* live candidate,
check the decision gate, and only then draw the next observation if neither
condition is met.

#### 5.3.1 Track Observation Feeder — Distance-Seeking Order

Within a bucket, rank available tracks before feeding the next observation,
in this priority order:
1. **Different album** over same album (avoids correlated risk from one mis-tagged release).
2. **Single-credit tracks** (exactly one `Artist` and one `AlbumArtist`) over multi-credit tracks — ordering preference only, not exclusion.
3. **Different track title** over repeated/similar titles (avoids weak signal from near-duplicate tracks).
4. **Longer track titles** over shorter ones — a short, generic title is more likely to produce weak/ambiguous corroboration.
5. **Shorter albums (< 20 tracks)** over longer ones — large-track-count releases are disproportionately compilations/box sets, more likely to have incomplete MusicBrainz data.

These rules affect **only feed order**, never the LLR sum — none of album
length, credit count, or title length says anything about candidate
correctness on its own, only about the likely cleanliness of the
observation.

### 5.4 Scoring Model

Bayesian, natural-log-odds (nats): `confidence = 1 / (1 + e^-cumulative_LLR)`,
starting at 0 (50/50) per candidate. The engine tracks the top candidate's
cumulative LLR and its margin over the runner-up's own cumulative LLR.

**User-facing translation layer** (required — never expose raw LLR in any
user-facing surface): bin each evidence contribution into one of
`Strong Positive · Positive · Weak Positive · Neutral · Weak Negative ·
Negative · Strong Negative`, always paired with a plain-language rationale
sentence (e.g. "Emby says this artist has this exact track on this exact
album, and MusicBrainz agrees on all three"). Raw LLR is Diagnostic-Mode-only
(§10.2).

### 5.5 Decision Gate

Three-way outcome, against `ScoringConfig` thresholds (default:
`AutoAcceptThreshold = +4.0` nats with `MinMarginOverRunnerUp = +2.0`;
`AutoRejectThreshold = -3.0` for every candidate):

- **Auto-accept**: top candidate crosses `AutoAcceptThreshold` with sufficient margin.
- **Auto-reject**: every candidate is at or below `AutoRejectThreshold` → write to `negative_cache`.
- **Needs-review**: neither condition met → write to the review queue with the full evidence trail attached.

### 5.6 Identity Cache & Propagation

On auto-accept or human-confirm, write to `identity_cache` keyed on the Emby
source ItemId (not name string). Any future observation of the same source
entity — including a weak, isolated Composer-only credit — first checks this
cache: a match with no contradiction short-circuits straight to reuse; a
contradiction reopens the case for full resolution. Periodic revalidation
(not per-track) guards against staleness as MusicBrainz data changes. This is
also the primary source of ongoing API-load savings once a library is
substantially resolved.

### 5.7 Active Learning Feedback Loop

- Every human decision on a needs-review item is a labeled example:
  `(evidence bag, chosen candidate or "none")`.
- **Immediate**: store as a permanent per-entity override — do not re-ask
  about the same entity unless explicitly requested.
- **Periodic (batch)**: recompute evidence weights from the accumulated
  labeled set; surface recalibration suggestions rather than auto-applying
  them (§16).
- **Pattern flag**: a recurring type of ambiguity is a developer-facing
  signal to add a new evidence type or generation strategy, not an
  automated code change.
- Keep `review_decisions` separate from `match_results` — training/audit
  data, never silently overwritten.

---

## 6. Evidence Catalog

All values are starting defaults, stored in `scoring_config_overrides` (§9)
and editable via the Developer page (§14.3) — not hardcoded constants.
Calibrate via §16 once real resolution volume exists.

### 6.1 Evidence Types and LLR Values (nats)

| Evidence | LLR | Notes |
|---|---|---|
| ProviderIds asserted MBID, confirmed | +5.0 | Tier 0 — confirmed via E4's ProviderIds field, §8.2. Stronger than Tier 1 because asserted by prior tagging, but still verified |
| ProviderIds asserted MBID, confirmation failed | −4.0 | Strong negative — a stale/wrong tag is informative |
| Disambiguation/context match | +0.8 | |
| Work relationship: writer/composer/lyricist | +2.5 | Sourced from `work-rels`+`work-level-rels`, §5.2/§7.2 |
| Recording relationship: producer | +0.8 | Sourced from `artist-rels` on the recording, §5.2/§7.2 |
| Recording relationship: arranger | +0.5 | Recording-level by MB convention, not work-level |
| Album match — distinctive title | +1.5 | |
| Album match — generic title | +0.3 | "Greatest Hits", self-titled, etc. |
| Corroboration Tier 1 — full triple (Artist+Track+Album) | +3.5 | Near-decisive alone; supersedes standalone album-match for the same album |
| Corroboration Tier 2 — Artist+Track, no album | +1.8 | Strong, not alone sufficient for auto-accept |
| Corroboration Tier 3 — single field only | +0.5 | Background support |
| Entity-type/life-span sanity — consistent | +0.2 | Minor |
| Entity-type/life-span sanity — contradicts | −3.0 | Near-disqualifying |
| Tag/genre overlap | +0.3 | Weak |
| External ID cross-link present (ISNI/IPI/Discogs) | +0.5 | Also seeds the future Discogs resolver |
| Anchor-by-association | anchor's own cumulative LLR × 0.7 | **Parked — not currently used, see §5.1 / §10.1 Anchoring.** Discounted — inherited, not independently re-derived, if/when reintroduced |

**Note**: name/alias similarity is NOT an evidence type in this table — see
§5.1/§5.2. It's a gateway (admission gate + corroboration multiplier), never
a standalone additive line.

**Known, deliberately deferred residual risk**: Corroboration Tier 1/2/3
above applies a flat LLR regardless of how distinctive the *track*/*album*
title actually is (unlike the album-match distinctive/generic split, which
already guards against this for the precursor). A same-named-artist
collision with a generic track title on a generic album could plausibly
survive the fallback ladder and pick up genuine weak corroboration against
the wrong candidate. See `Evidence_Log.md` for the investigation. Proposed
future tweak, not built: extend the distinctive/generic treatment into
`CorroborationTierEvidenceCollector`'s own weighting, and consider track
duration as an additional signal once carried on `EmbyTrackCredit`.

### 6.2 Role Weight Multipliers

| Role | Multiplier |
|---|---|
| AlbumArtist | 1.0× |
| Artist (performer) | 1.0× |
| Composer, undecomposed/other relationship | 1.0× |

Neutral by design (see `Decisions.md`), not a placeholder awaiting tuning —
kept as a live, editable `ScoringConfig.RoleWeights` surface.

### 6.3 Corroboration Tiers

Categorical, not continuous — encode as a specific evidence type with the
tier as its value:
- **Tier 1**: Emby's Artist + Track + Album all agree with MusicBrainz. Near-decisive alone.
- **Tier 2**: Artist + Track agree, no album corroboration. Strong, needs at least one supporting signal for auto-accept.
- **Tier 3**: single field match only. Weak, background support.

**Match-quality multiplier**, applies to all three tiers above: before
role-weighting (§6.2), multiply the tier's LLR by
`ScoringConfig.NameMatchWeight` (default 1.0) if the recording result's
artist-credit matched the candidate's primary name, or
`ScoringConfig.AliasMatchWeight` (default 0.9) if it matched only via one of
the candidate's registered aliases (`EvidenceRecord.MatchedViaAlias`, §5.2).
This is the entirety of how alias-vs-name match quality affects scoring.

### 6.4 Decision Thresholds

- `AutoAcceptThreshold`: +4.0 nats, **and** `MinMarginOverRunnerUp`: +2.0 nats over the runner-up candidate's own cumulative LLR.
- `AutoRejectThreshold`: −3.0 nats, for every remaining candidate.
- Otherwise: needs-review.

---

## 7. MusicBrainz Integration

### 7.1 Identification and Rate Policy

- `ApiPolicy.UserAgentString` (required) — an identifying User-Agent (e.g.
  `MetadataHealthCheck/2.0 (contact-url)`). Unidentified clients share a
  lower rate allowance.
- `ApiPolicy.MusicBrainzBaseUrl` (default `https://musicbrainz.org/ws/2/`) —
  configurable to point at a private proxy/mirror.
- `ApiPolicy.RateLimitEnabled` (default true) — set false when using a proxy
  with no rate limit. Even when disabled, retain the request queue/backoff
  mechanism for transient errors — only bypass the throttle.
- `ApiPolicy.RequestsPerSecond` (default 1.0) — hard ceiling when enabled;
  burst above it gets rejected, not gracefully queued by MusicBrainz.

### 7.2 API Call Catalog

This is a library of known available calls, not a prescribed sequence —
which calls a given resolution path actually uses, and in what combination,
is determined by implementation and testing, not specified in advance.

| # | Call | Triggered By | Yields | Feeds |
|---|---|---|---|---|
| **C1** | `GET /ws/2/artist?query=artist:"{name}"&fmt=json` | Stage 1 admission (§5.1), once at the start of every resolution task | Candidate list: MBID, name, sort-name, disambiguation, type, area, life-span, registered aliases (present inline, no extra `inc=` needed), MB's own text-relevance `score` | Load-bearing for Stage 1 admission (§5.1) — name/alias match against this response is the admission bar |
| **C2** | `GET /ws/2/artist/{mbid}?inc=tags+release-groups+url-rels&fmt=json` | Once per candidate surviving Stage 1 | Tags, release-groups, url-rels (external IDs) | Tag-overlap, album-match, external-ID evidence. Release-group/release browse endpoints **require** an explicit `type=`/`status=` filter or return empty — do not treat an unfiltered empty result as "no albums found." |
| **C3** | `GET /ws/2/recording?query=recording:"{track}"+AND+release:"{album}"&fmt=json` (fallback: drop `release:`) | Stage 2 confirmation (§5.1), name-bearing query variant — AlbumArtist/Artist-tier observations, per-candidate confirmation step, fallback ladder | Matching recording(s), usually one: artist-credit, length, release-list | Duration filter; Tier 2/3 corroboration data included at no extra cost |
| **C4** | Same call shape as C3, track+album only, no artist field | Stage 2 confirmation (§5.1), relationship-scan query variant — Composer-tier observations | Recording candidates for relationship mining | Feeds C5 |
| **C5** | `GET /ws/2/recording/{id}?inc=artist-credits+artist-rels+work-rels+work-level-rels&fmt=json` | On the surviving recording candidate from C3/C4 | Work-level relations (composer/lyricist/writer) **and** recording-level relations (producer/engineer/arranger) | Composer-relationship LLR breakdown, §6.1; MBID confirmation for relationship-scan matches |
| **C6** | `GET /ws/2/artist/{candidate_mbid}?inc=artist-rels&fmt=json` | After C5 surfaces a writer/composer MBID | Artist-to-artist relationship list | Supporting corroboration check |

### 7.3 Unverified — Check Before Building

- Exact `inc=` parameter for ISNI/IPI identifier codes — not confirmed.
- Whether any relationship-type names referenced above have since been split into more specific subtypes (MusicBrainz has done this before, e.g. with "engineer").

---

## 8. Emby Integration

### 8.1 Cost Model

Server-side plugin: Emby API access means injected C# service interfaces
(`ILibraryManager`, etc.), **not HTTP calls** — no network round-trip, no
rate limit. The real cost driver is field-hydration breadth, not per-artist
query count: a single artist's own track read costs tens of milliseconds,
and the pipeline is already strictly sequential (§12.1) and bottlenecked by
MusicBrainz's 1 req/sec ceiling (§7.1) regardless.

**Two separate reads, not one broad pass, deliberately:**
1. A **lightweight, library-wide Tier Classification Pass** (name/id/role
   only, no track-level hydration) — run once per sync, fixes the
   wavefront's processing order only. Provisional and non-authoritative.
2. A **full Per-Artist Track Read**, done at processing time, one artist at
   a time — never upfront for the whole library.

### 8.2 API Call Catalog

| # | Call | Triggered By | Yields | Feeds |
|---|---|---|---|---|
| **E1** | `ILibraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { typeof(MusicArtist).Name }, Recursive = true, IsVirtualItem = false, EnableTotalRecordCount = false, DtoOptions = new DtoOptions(true) })` | Once per full library sync | Full artist list: Id, Name, sort-name — complete, includes composer-only artists | Enumeration set the wavefront iterates over; `EmbyArtistProvider.GetAll()` needs no union with any other source |
| **E1b** | `_libraryManager.GetAlbumArtists(...)` / `_libraryManager.GetArtists(...)` — library-wide, no `ParentId` | Once per full library sync | Which `MusicArtist` ids are album artists, which are (any) artists | Provisional `artist_role_classification` (§9) |
| **E1c** | `_libraryManager.GetItemList(new InternalItemsQuery(user) { IncludeItemTypes = new[] { typeof(Person).Name }, PersonTypes = new[] { "Composer" }, Recursive = true })` | Once per full library sync | `Person` items tagged `Composer`, ids drawn from the same id space as `MusicArtist` | Provisional Composer-tier signal — authoritative role always comes from E2 |
| **E2** | `ILibraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { typeof(Audio).Name }, Recursive = true, ArtistIds/AlbumArtistIds/ComposerArtistIds = new[] { internalId } })` — exact filter shape TBD, see §8.3 | Once per artist, at that artist's own processing time | This artist's tracks only: Name, Album, AlbumArtists, Artists, People, RunTimeTicks, ProviderIds, ParentId | Authoritative role reclassification, per-track bucketing (§5.3.1), incidental co-occurrence discovery, duration checks, ProviderIds fast path |
| **E3** | `ILibraryManager.GetItemById(Guid id)` | Targeted re-check only | Single item DTO | Point verification — must not be used in a loop |
| **E4** | (field within E2's result, no separate call) | Every track, incidentally | `ProviderIds` dictionary | Tier 0 evidence, §6.1 |

**Tier assignment from E1b/E1c** (priority AlbumArtist > Artist > Composer, exclusive):

```
albumArtistIds  = GetAlbumArtists() ids
allArtistIds    = GetArtists() ids
composerIds     = E1c Person/Composer ids

artistOnlyIds   = allArtistIds - albumArtistIds
composerOnlyIds = composerIds  - allArtistIds
```

E1 alone (`GetItemList` filtered to `IncludeItemTypes=MusicArtist`) is
sufficient for enumeration — no composer-only artist is invisible to it.
`EmbyArtistProvider.GetAll()` needs no defensive union with E1c.

**Requirement**: E2 is deliberately a per-artist query, issued fresh each
time that artist is processed (§8.1). Within a single artist's resolution,
downstream stages read from that artist's own fresh E2 result directly; no
whole-library track cache is kept anywhere in the engine.

**Incremental sync**: run as a scheduled task (§12), following the reference
plugin's idiom, rather than reacting to live library-change events.

### 8.3 Unverified — Check Before Building

- Whether the dedicated `GetArtists()` method (distinct from raw
  `GetItemList`, E1) excludes composer-only artists. Low-stakes either way
  (§8.1) — a wrong provisional tier costs nothing downstream.
- Whether to use the C# `ILibraryManager` calls or Emby's REST API for
  E1b/E1c. REST auth mechanism is confirmed (admin-supplied API key +
  `IHttpClient`, sent as `X-Emby-Token`).
- Whether E1c's returned `Person` items always carry a usable internal id in
  the same numeric id-space as `MusicArtist` for the composer-only case.

---

## 9. Storage

SQLite, via `SQLitePCL.pretty` (matching the reference plugin
`Emby.AutoOrganize`). Single shared write connection guarded by a
`ReaderWriterLockSlim`; read connections cloned off it. `PRAGMA
journal_mode=WAL` and `PRAGMA synchronous=Normal` on initialization.
Database file at `Path.Combine(appPaths.DataPath, "metadatahealthcheck-v2.db")`
— entirely separate from the existing prototype's store. Schema via `CREATE
TABLE IF NOT EXISTS` / `CREATE INDEX IF NOT EXISTS`; migrations via a
`GetColumnNames` + `AddColumn` helper, matching the reference plugin's
pattern rather than introducing a separate migration framework.

### 9.1 Full Table List

```
schema_version          (version INTEGER)   -- checked/bumped in Initialize(), tracks schema (not data) migrations

source_entities         (source_system, source_type, source_id, display_name, ...)
resolution_candidates   (task_id, source_entity_id, target_system, target_type,
                         target_id, confirmation_query_shape, generation_query, created_at)
evidence                (candidate_id, evidence_type, raw_value, role, source_track_id,
                         album_id, relationship_type, rationale)
scores                  (candidate_id, running_llr, computed_at, scoring_config_version)
match_results           (source_entity_id, target_system, target_type, target_id,
                         status[auto_accept|auto_reject|needs_review|human_confirmed|human_rejected],
                         confidence, margin, scoring_config_version, decided_at, decided_by)
review_decisions        (match_result_id, human_choice, decided_at, notes)   -- active-learning label set; never overwritten by any clear operation except an explicit, separately-confirmed one (§14.2)
negative_cache          (source_entity_id, target_system, reason, checked_at, recheck_after)
api_cache               (target_system, endpoint, query_hash, response_json, fetched_at, ttl)
identity_cache           (source_entity_id, target_system, target_id, confidence, confirmed_at, source)
artist_role_classification (artist_id, highest_role, source[provisional|authoritative], classified_at)
                        -- 'provisional' rows come from E1b/E1c (§8.2), used only to order the wavefront;
                        -- 'authoritative' rows come from each artist's own E2 read (§8.2) and supersede
                        -- the provisional row for that artist once processed
artist_cooccurrence     (artist_id, cooccurring_artist_id, recording_id, role_of_cooccurring)
                        -- populated incrementally, as each artist's own E2 read surfaces co-occurring
                        -- credits — there is no global co-occurrence pre-pass (§3.2, §8.1). Independent
                        -- of the parked anchoring concept (§5.1/§10.1) — this table is populated
                        -- regardless of whether anchoring logic is ever implemented.
anchor_dependencies     (dependent_artist_id, anchor_artist_id, anchor_confidence_at_use, resolved_at)
                        -- parked — see §5.1/§10.1. Retained in schema, not currently written to.
content_fingerprints    (item_id, fingerprint_hash, item_type, computed_at)   -- guards against Emby item-id drift across library rescans
scoring_config_overrides (key, value, updated_at)   -- §10.3, editable tuning values; also holds "suggested_" prefixed rows from the calibration job, §16
```

`api_cache` and `negative_cache` mean a re-run only queries what's new or
stale — no raw MusicBrainz response should ever be fetched twice for the
same query.



## 10. Configuration

Three separate objects, on two separate pages (§13):

### 10.1 EngineConfig (main user-facing page)

```
EngineConfig
├── ApiPolicy
│   ├── MusicBrainzBaseUrl: string (default "https://musicbrainz.org/ws/2/")
│   ├── RateLimitEnabled: bool (default true)
│   ├── RequestsPerSecond: float (default 1.0, ignored if RateLimitEnabled=false)
│   ├── UserAgentString: string (required)
│   └── CacheTtlByEndpoint[], NegativeCacheRecheckInterval
├── EmbyRestFallback   -- only relevant if the dedicated GetArtists()/GetAlbumArtists() C# calls prove awkward for E1b/E1c (§8.2, §8.3)
│   ├── Enabled: bool (default false — prefer the ILibraryManager calls unless/until needed)
│   ├── ApiKey: string (required if Enabled; admin-supplied, sent as X-Emby-Token, §8.2)
│   └── BaseUrl: string (default derived from the server's own configured address)
├── Anchoring   -- PARKED: not wired into candidate generation/confirmation logic anywhere in this
│   │             spec (§5.1). Retained for future validation once the core pipeline is proven out.
│   ├── Enabled: bool (default false)
│   ├── CooccurrenceGraphRefreshInterval
│   └── AnchorDependencyTrackingEnabled: bool
└── SelfCritique
    ├── EnableCalibrationBacktest: bool
    ├── BacktestSchedule
    └── MinGroundTruthSampleForRecalibration
```

### 10.2 DeveloperConfig (developer page)

```
DeveloperConfig
├── ArtistFilter: string       -- comma-separated; each token tried as a Guid first, falling back to case-insensitive name match; empty = no restriction
├── AlbumFilter: string        -- same parsing rule
├── DiagnosticMode: bool
├── DisableCacheInDiagnosticMode: bool
└── RetainRawApiResponses: bool
```

Applied inside `EmbyArtistProvider.GetAll()` (§11.3) — if `ArtistFilter` is
non-empty, return only matching entities. No other pipeline component needs
to know a filter is active.

### 10.3 ScoringConfig (code-based for now; UI-editable grid deferred, see `Decisions.md`)

Row-shaped tuning data, one entry per §6 value, defaulting to the values
listed there if absent:

```
ScoringConfig
├── CandidateGeneration.StrategyA/B.MaxCandidates, DurationToleranceSeconds
├── CandidateGeneration.ArtistCandidateMinScore: int (default 0, inert — MB's own C1 relevance score, §5.1/§5.2, reserved)
├── CandidateGeneration.ArtistCandidateMaxEditDistance: (Stage 1 admission gate, §5.1 — needs real-data tuning, no default yet asserted as correct)
├── CandidateGeneration.NameNormalizationRules[]   -- editable replacement/allowance table, §5.1 Stage 1
├── NameMatchWeight: double (default 1.0), AliasMatchWeight: double (default 0.9)   -- Stage 2 corroboration multiplier, §5.1/§6.3
├── BucketCeiling { AlbumArtist: 3, Artist: 4, Composer: 6 }
├── ComputeAlbumMatchPrecursorOncePerCandidate: bool
├── PreferDifferentAlbum, PreferSingleCreditTracks, PreferDifferentTrackTitle, PreferLongerTrackTitles: bool
├── PreferAlbumTrackCountBelow: int (default 20)
├── AutoAcceptThreshold, AutoRejectThreshold, MinMarginOverRunnerUp
├── EvidenceWeights[]   -- one row per §6.1 evidence type
├── RoleWeights { AlbumArtist: 1.0, Artist: 1.0, Composer: 1.0 }   -- neutral default, §6.2
├── ComposerRelationshipWeights { writer, producer, arranger, other }
├── AlbumMatchWeights { distinctiveTitle, genericTitle }
└── JointEvidencePairs[]   -- correlated-pair overrides, §5.2 (album-match/full-triple supersession only)
```

*(Note: the `CandidateGeneration.StrategyA/B.MaxCandidates` label above is a
pre-existing config path name carried over from before generation strategies
were removed from the pipeline narrative, §5.1. It refers to candidate-count
caps in the Stage 1/Stage 2 mechanism, not to any surviving "strategy". Rename
if it causes confusion during implementation — flagged rather than changed
here since it's a config key name, not prose.)*

---

## 11. Class Structure

### 11.1 Folder / Namespace Layout

All new code under `MetadataHealthCheck.v2`, entirely separate from the
existing prototype. see later push on github.

### 11.2 Core Interfaces

None of these mention Emby, MusicBrainz, Artist, or Album by name — this is
what makes §11.4's extensibility requirement checkable.

`IObservationUnit`, `IObservationEvidenceCollector`, and
`IObservationUnitProvider` support §5.3's per-observation sampling
generically. `IObservationUnit` is deliberately opaque to `Core` — for the
Artist/MusicBrainz case a unit is one track and `BucketKey` is
"AlbumArtist"/"Artist"/"Composer"; a future entity type with no natural
role/bucket concept can either use a single constant `BucketKey` or not
implement `IObservationUnitProvider` at all (`IResolverPlugin.ObservationUnitProvider`
is nullable for exactly this reason). When both are absent, the engine
scores from static evidence alone.

See reference for interface-level documentation. System level interfaces are in `MetadataHealthCheck.v2.Core.Interfaces`.

### 11.3 Requirements: Source Population & Filter Application Point

`EmbyArtistProvider.GetAll()` (§11.2) enumerates directly from E1's
`MusicArtist` list — complete, including composer-only artists (§8.2), so no
union with any other source is needed. E1b/E1c (§8.2) are consulted only for
provisional tier tagging of ids E1 already returned.

`DeveloperConfig.ArtistFilter`/`AlbumFilter` (§10.2) is applied **only**
inside the `ISourceEntityProvider<TSourceEntity>` implementation for the
relevant source type — no other layer inspects or is aware of it.

### 11.4 Extensibility Requirement

Adding a new target system or source entity type must require **zero
changes** to `Core/`, `Storage/`, or the engine orchestration. Verify by
implementing both cases below before considering the architecture validated:

- **New target system (e.g. Discogs), same entity type**: new
  `Resolvers/Discogs/` folder, `DiscogsArtistResolverPlugin : IResolverPlugin<EmbyArtist>`
  with its own strategies/evidence collectors. `match_results.target_system`
  gains a new value; no schema migration required.
- **New source entity type (e.g. Album), same target system**: new
  `EmbyAlbum : ISourceEntity` and `EmbyAlbumProvider : ISourceEntityProvider<EmbyAlbum>`,
  plus `MusicBrainzAlbumResolverPlugin : IResolverPlugin<EmbyAlbum>` with its
  own evidence collectors (e.g. track-listing overlap instead of
  work/writer relationships). `EmbyAlbumProvider` simply omits an
  `IProcessingOrderStrategy` implementation, since Album has no "role"
  concept.

---

## 12. Task Invocation & Composition Root

### 12.1 Concurrency

**None.** The entire pipeline — sync task, tiered wavefront, per-entity
resolution, sequential sampler — runs strictly sequentially: one artist and
one observation at a time.

### 12.2 Scheduled Tasks

`IScheduledTask` is the invocation mechanism:

```csharp
namespace MetadataHealthCheck.v2.Tasks
{
    public class MetadataHealthCheckSyncTask : IScheduledTask, IConfigurableScheduledTask
    {
        public string Name => "MetadataHealthCheck: Resolve Artists";
        public string Category => "Library";
        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress) { /* §3.2's pipeline */ }
    }
}
```

Registering this gets a scheduled/recurring run **and** a manual "Run Now"
trigger for free from Emby's own admin dashboard — no custom trigger UI is
required. `CalibrationBacktestTask` registers as a second, independent
`IScheduledTask`, since it never touches the network and can run on its own
schedule.

### 12.3 Composition Root

- Implement `IServerEntryPoint` (e.g. `EngineEntryPoint.cs`). Its
  constructor is auto-injected by Emby's container with Emby's own native
  services (`ILibraryManager`, `ITaskManager`, `ILogger`,
  `IServerConfigurationManager`, etc.).
- Inside `Run()`, manually construct the engine's own services in
  dependency order: `MatchRepository` (needs `IServerConfigurationManager.ApplicationPaths`
  for the db path) → `MusicBrainzApiClient` → each `IResolverPlugin`
  implementation → `ResolutionEngine`. There is no generic
  custom-service-registration mechanism in this SDK generation — manual
  construction is the idiomatic pattern.
- Expose the constructed `ResolutionEngine` via a static
  `EngineEntryPoint.Current` singleton. `MetadataHealthCheckSyncTask`
  (itself Emby-native-DI-injected) and `DeveloperToolsService`
  (ServiceStack-resolved) both reach it this way.

---

## 13. Config Pages

### 13.1 Main Page — `BasePluginSimpleUI<EngineConfig>`

`EngineConfig : EditableOptionsBase` (§10.1). Implementing `Plugin :
BasePlugin<EngineConfig>` auto-generates the settings form from the
class's properties — no HTML/JS required.

**Explicit limitation** (stated in Emby's own docs): this mechanism does not
support custom button actions or multi-page complexity — why it's used only
for `EngineConfig`, not the Developer page.

### 13.2 Developer Page — IHASUIPAGES

Required because of §13.1's limitation — the Developer page needs button
actions (§14) and grid-shaped data (§14.3).

### 13.3 Developer Page Contents

- `DeveloperConfig` fields (§10.2): `ArtistFilter`, `AlbumFilter` text inputs, `DiagnosticMode` and related toggles.
- Granular clear-operation buttons (§14).
- Scoring/weighting tuning grid (§14.3).

---

## 14. Developer Tools

### 14.1 Route

One flexible endpoint covers all clear operations and the scope modifier —
do not create one route per operation.

```csharp
[Route("/MetadataHealthCheck/v2/Developer/Clear", "DELETE", Summary = "Selectively clears v2 engine data for testing")]
public class ClearDeveloperData : IReturnVoid
{
    [ApiMember(Name = "What", IsRequired = true, DataType = "string", ParameterType = "query", Verb = "DELETE",
        Description = "Comma-separated: rescore, recandidates, apicache, negativecache, identitycache, anchors, reviewdecisions, all")]
    public string What { get; set; }

    [ApiMember(Name = "ArtistFilter", IsRequired = false, DataType = "string", ParameterType = "query", Verb = "DELETE",
        Description = "Optional comma-separated artist names/ids to scope the clear to. Empty = whole library.")]
    public string ArtistFilter { get; set; }
}
```

### 14.2 Operations

Ordered from cheapest/most-preserving to most-destructive. Each is a
distinct button on the Developer page, not a raw text field for `What`.

| Operation | Clears | Keeps | Purpose |
|---|---|---|---|
| **Re-score** | `scores`, `match_results` | `resolution_candidates`, `evidence` | Tuning LLR values/thresholds — zero network calls, pure recomputation. The primary operation, paired with the scoring grid's Save button (§14.3). |
| **Re-resolve from cache** | + `resolution_candidates`, `evidence` | `api_cache` | Candidate-generation or evidence-collection code changed — re-runs logic, serves from cache wherever possible |
| **Clear API cache** | `api_cache` | Everything else | Suspected stale MB data |
| **Clear negative cache** | `negative_cache` | Everything else | Retry previously-rejected entities |
| **Clear identity cache** | `identity_cache` | Everything else | Test propagation/short-circuit logic (§5.6) in isolation |
| **Clear anchor dependencies** | `anchor_dependencies` | Everything else | Test cascade-reopen logic in isolation — **parked feature (§5.1/§10.1)**; this operation clears a table not currently written to |
| **Clear review decisions** | `review_decisions` | Everything else | **Always separate, always explicitly confirmed in the UI** — this is the active-learning ground truth; never bundled into any other operation |
| **Full wipe** | Everything, including review decisions | Nothing | True start-from-zero |

**Scope modifier**: every operation above can run against the whole library
or be scoped to `DeveloperConfig.ArtistFilter`'s current value.

### 14.3 Scoring/Weighting Tuning Grid

Plain HTML tables with number inputs are sufficient. Sections, each editable:

- Evidence LLR values (§6.1) — one row per evidence type.
- Role weights (§6.2) — three rows.
- Decision thresholds (§6.4).
- Sampling — bucket ceilings, the five distance-seeking toggles (§5.3.1), `PreferAlbumTrackCountBelow`.
- Joint evidence overrides (§5.2) — shown as pairs with their combined value, not as independently-multiplied components.

**Two required actions**:
- **"Save & Re-score"** — persists edits (bumping `scoring_config_version`), then immediately triggers the Re-score operation (§14.2), scoped to `ArtistFilter` if set.
- **"Reset to defaults"** — reverts `scoring_config_overrides` to §6's built-in starting values.

**Calibration integration**: when the backtest job (§16) determines a value
is mis-calibrated, it writes a `suggested_`-prefixed row rather than
overwriting the live value. The grid shows a "suggested" column wherever the
backtest has an opinion, with a per-row "accept suggestion" action.

---

## 15. Logging

### 15.1 Logger

`ILogger` exposes `Debug`, `Info`, `Warn`, `ErrorException(message, exception, args)`
— indexed `{0}` placeholders, no separate `Trace` level. One logger
instance, scoped to the name **`MetadataHealthCheck`**, not per-class
loggers with different root names.

**Unverified — check before building**: the exact call to obtain a logger
scoped by name.

### 15.2 Principle

Log by default; filter by level, not by omission. Any function whose
outcome matters to a power user, an admin, or a developer gets a log line at
the appropriate level.

### 15.3 Level ↔ Audience

| Level | Audience | Content |
|---|---|---|
| `Info` | Power user | One line per resolved entity — outcome only |
| `Info` | Admin | Lifecycle events — run start/end, counts, durations |
| `Warn` | Admin | Recoverable anomalies — rate-limit backoff, cascaded re-opens |
| `ErrorException` | Admin/Developer | Failed calls, exceptions |
| `Debug` | Developer | Per-candidate detail — strategy attempts, evidence LLR contributions, sampler running total, decision math. Raw request/response payloads gated behind `DiagnosticMode`/`RetainRawApiResponses` (§10.2). |

### 15.4 Component Prefix

Every line carries a `[ComponentName]` prefix, mapped 1:1 to §11.1's folder structure:

`[Sync] [Engine] [Sampler] [CandidateGen] [Evidence] [Scorer] [DecisionGate] [MBClient] [EmbyReader] [IdentityCache] [Anchor] [ActiveLearning] [Calibration] [Config]`

### 15.5 Structured Log = Same Event, Two Outputs

`Diagnostics/StructuredLogger.cs` is the single call site: it writes the
structured evidence record to SQLite (§9, feeding the future review UI and
the calibration job) **and** emits the human-readable `[Component] ...` line
to `ILogger` at the matching level, from one call.

---

## 16. Calibration (Self-Critique)

Runs as `CalibrationBacktestTask` (§12.2), against cached evidence only — no network calls.

- **Calibration check**: group all decisions by their confidence at decision
  time into buckets (e.g. 90–95%, 95–100%). For each bucket, check what
  fraction were actually correct once known. An overconfident bucket
  signals lowering the relevant evidence weight; underconfident signals a
  threshold could safely be lowered.
- **Sampling-sufficiency check**: for a sample of auto-accepted matches,
  replay a wider bucket ceiling or an alternate confirmation-query variant
  against cached data — under/over-sampling signal.
- **Anchor-trust check**: parked — deferred until anchoring (§5.1/§10.1) is implemented.
- Output is a **report and suggested `scoring_config_overrides` values**
  (§14.3) — never an automatic change to live weights/thresholds.

---

## 17. Testing Strategy
Not yet introduced.

## 18. Worked Example (Validation Reference)
Moved to Reference.MD

## 19. Phased Build Plan

Building the entire spec simultaneously is not a realistic first milestone.
Build in this order; each phase is independently testable.

1. **Skeleton + one path end-to-end**: `Core` model/interfaces (§4, §11.2),
   SQLite storage (§9, core tables only), `Sources/Emby` (§8),
   `Resolvers/MusicBrainz` with only the Stage 1/Stage 2 mechanism (§5.1)
   and only two evidence types. A simple weighted-sum scorer is acceptable
   here — Bayesian comes in phase 2. Goal: one artist resolved end-to-end,
   logged, stored.
2. **Full evidence set + Bayesian scoring**: remaining evidence collectors
   (§6), switch to `BayesianBeliefScorer`, add the sequential sampler (§5.3)
   and decision-gate margins. Goal: reproduce §18's worked example exactly.
3. **Tiering**: provisional tier-classification pass (E1b/E1c, §8.2) for
   wavefront ordering, authoritative per-artist reclassification and
   incremental co-occurrence discovery from each artist's own E2 read
   (§8.1–§8.2, §9). Goal: composer-only artists resolving via the
   relationship-scan confirmation variant (§5.1) — anchoring (§10.1)
   remains parked and out of scope for this phase.
4. **Active learning + calibration**: review queue, identity cache,
   calibration backtest job (§5.7, §16).
5. **Developer tooling + config pages**: `EngineConfig` baseplugin<pluginconfiguration> page,
   Developer HTML/JS page with filters/granular clears/scoring grid
   (§13–14). Deliberately last.

Current progress against these phases lives in `V2_Project_Log.md`, not here.
# MetadataHealthCheck v2 — Entity Resolution Engine
## Build Specification

*This is the implementation spec. It supersedes casual reasoning from any prior discussion — if you need the rationale behind a decision, a separate design-history document exists, but this document is authoritative for what to build.*

---

## 1. Purpose & Scope

Build an entity resolution engine that maps Emby library entities to external metadata identifiers, starting with **Emby Artist → MusicBrainz Artist**. The engine is a generic framework, not a single-purpose script: adding a second target system (e.g. Discogs) or a second source entity type (e.g. Emby Album) must require no changes to the core pipeline, only new implementations of existing interfaces.

**Priority order, in case of conflict between them:**
1. Accuracy of identification
2. Development clarity (logging, evidence traceability)
3. User clarity (explainable evidence and confidence, in plain language)
4. Minimizing load on external APIs
5. Client/runtime performance

**Deployment constraint**: this is a new engine (`MetadataHealthCheck.v2`) built alongside an existing working prototype. It must not modify, read from, or depend on the prototype's data store. It runs against its own SQLite database, has its own configuration, and is validated by comparison against the prototype before cutover (§19).

### Contents
1. Purpose & Scope — 2. Glossary — 3. Architecture Overview — 4. Data Model — 5. Resolution Pipeline — 6. Evidence Catalog — 7. MusicBrainz Integration — 8. Emby Integration — 9. Storage — 10. Configuration — 11. Class Structure — 12. Task Invocation & Composition Root — 13. Config Pages — 14. Developer Tools — 15. Logging — 16. Calibration — 17. Testing Strategy — 18. Worked Example — 19. Deployment & Cutover — 20. Assumptions to Verify — 21. Phased Build Plan

**If implementing this, read in this order**: §21 (build plan) → §3–4 (architecture/data model) → §11 (class structure) → §5 (pipeline detail) as each phase is reached → remaining sections as needed.

---

## 2. Glossary

| Term | Meaning |
|---|---|
| **Candidate** | A possible MusicBrainz match for a source entity, prior to scoring |
| **Evidence** | One observed fact about a candidate (name match, work-writer relationship, album overlap, etc.), stored as a raw fact, not a pre-computed score |
| **LLR** | Log-likelihood ratio, natural-log (nats) units — the unit every evidence value is expressed in; summed to produce a running confidence |
| **Corroboration Tier** | Evidence-strength category: Tier 0 (asserted MBID in file tags), Tier 1 (full triple: Artist+Track+Album agree), Tier 2 (Artist+Track agree), Tier 3 (single field only) |
| **Processing Tier / Bucket / Role** | Which credit type a track observation came from: AlbumArtist, Artist, or Composer — also determines processing order (highest-signal role first) |
| **Strategy A / B / C** | Candidate-generation approaches: A = own confirmed anchor, B = broad fallback search, C = borrowed anchor from a co-occurring, already-resolved artist |
| **Anchor** | A confirmed MBID used to precisely restrict a MusicBrainz query (the `arid:` Lucene field) |
| **Sequential Sampler** | The adaptive, one-observation-at-a-time evidence loop that stops as soon as cumulative confidence crosses an accept/reject bound — this is the central resolution mechanism |
| **Identity Cache** | Store of already-confirmed source→target mappings, checked before any new resolution work begins |
| **Negative Cache** | Store of confirmed non-matches, to avoid re-querying dead ends |

---

## 3. Architecture Overview

### 3.1 Three Decoupled Axes

The engine is built around three independent axes. No component may assume a specific combination of these — the pipeline, storage schema, and interfaces are identical regardless of which combination is active.

| Axis | Phase 1 value | Future values |
|---|---|---|
| Source entity type | Emby Artist | Emby Album, Emby Track |
| Target system | MusicBrainz | Discogs, TheAudioDB |
| Target entity type | MusicBrainz Artist | MusicBrainz Release-Group, MusicBrainz Recording |

A resolution task is always `(SourceEntity, TargetSystem, TargetEntityType) → MatchResult`.

### 3.2 Pipeline

```
════════════════════════════════════════════════════════════════════
 LIBRARY SYNC (scheduled task — runs once per invocation, not per-artist)
════════════════════════════════════════════════════════════════════
  Single recursive Emby query over all Audio items (§8.2, call E2)
        │
        ▼
  Build in-memory: tracks grouped by artist, with role/album/duration/
  ProviderIds/People all attached in one pass
        │
        ▼
  Persist/refresh: artist_role_classification, artist_cooccurrence (§9)
        │
        ▼
  Classify every artist: highest role = AlbumArtist > Artist > Composer


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
  │  1. ProviderIds fast path (§5.1, Tier 0) — asserted MBID present? │
  │     Confirm with one anchored lookup; if confirmed, done.        │
  │  2. Album-match precursor (§5.2) — one cheap call per candidate.  │
  │  3. Candidate generation — Strategy A → C (Composer tier only)    │
  │     → B, in that priority order (§5.3).                           │
  │  4. Sequential sampler (§5.5) — one observation at a time,        │
  │     stop as soon as accept/reject bound is crossed.               │
  │  5. Decision gate (§5.7) — auto-accept / auto-reject /            │
  │     needs-review.                                                 │
  └───────────────────────────────────────────────────────────────┘
        │
        ├── auto-accept ──► identity_cache + match_results
        │                   (if via Strategy C: record anchor_dependencies)
        ├── auto-reject ──► negative_cache
        └── needs-review ─► review queue ──(human decision)──┐
                                                                │
        ┌───────────────────────────────────────────────────────┘
        ▼
  ACTIVE LEARNING FEEDBACK (§5.9)
     - permanent override recorded immediately
     - periodic: recompute LLR weights from accumulated labels
     - periodic: calibration backtest (§16) — histogram confidence buckets
       against known outcomes; replay alternate strategies against
       cached evidence to check under/over-sampling
     - if an anchor is overturned → cascade re-open via anchor_dependencies
```

---

## 4. Data Model

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
        public string GenerationStrategy { get; set; }     // "A" | "B" | "C"
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
        public string Rationale { get; set; }                  // human-readable sentence — always populated, §5.6
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

---

## 5. Resolution Pipeline

### 5.1 ProviderIds Fast Path (Tier 0)

Before any candidate generation: check the source track's `ProviderIds` for an existing MusicBrainz identifier (commonly written by taggers like Picard at rip time, surfaced by Emby's own item data — §8.3). If present, issue one anchored recording lookup (§7.2, call C3) to confirm it. On confirmation, record it as Tier 0 evidence (LLR +5.0, §6) and skip candidate generation entirely. On failure (asserted ID doesn't resolve), record as strong negative evidence (LLR −4.0) and continue to normal candidate generation — do not treat a failed assertion as fatal, since it's informative on its own.

### 5.2 Album-Match Precursor

Once per candidate (not per track): fetch the candidate's MusicBrainz release-group list (§7.2, call C2) and string-match against Emby's known album list for the source artist. Weight distinctive titles higher than generic ones (e.g. "Greatest Hits", self-titled — §6). This is the first observation fed into the sequential sampler (§5.5) — if it alone crosses the accept bound, no track-level sampling is needed at all.

**Supersession rule**: if a later observation produces a Tier 1 full-triple corroboration (§6.3) for the same album, that observation supersedes this precursor's contribution for that album — do not count both, since a full triple already implies the album matched.

### 5.3 Candidate Generation Strategies

Three strategies, tried in this priority order:

- **Strategy A (own anchor)**: if the source artist already has a confirmed MBID anchor, query `recording:"{track}" AND arid:{anchor_mbid}` (§7.2, call C3). Near-certain single hit. Even a single hit is passed through full evidence scoring — an anchor does not bypass verification.
- **Strategy C (borrowed anchor — Composer tier only)**: if no own anchor exists, but a co-occurring artist on the same recording (from the `artist_cooccurrence` table, §9) is already resolved, use *that* artist's confirmed MBID as the anchor for the same query shape as Strategy A. Record the anchor dependency (§9, `anchor_dependencies` table) with the anchor's confidence at time of use, so an overturned anchor can trigger a cascade re-open.
- **Strategy B (broad fallback)**: if neither A nor C is available, query `recording:"{track}" AND release:"{album}"` (falling back to dropping the `release:` clause if empty) (§7.2, call C4). Broader result set, tighter duration filter (§5.4).

All strategies return candidates tagged with generation provenance (strategy name, literal query) for logging (§15) and evidence traceability.

### 5.4 Evidence Collection Rules

- Every evidence collector implements `IEvidenceCollector<TSourceEntity>` (§11.2) and returns an `EvidenceRecord` with the **raw observed fact**, never a pre-baked LLR value — the LLR is looked up from the current `ScoringConfig` (§10.3) at scoring time. This is required for the "re-score without re-fetching" developer operation (§14.2) to work.
- **Static (candidate-pair-level) evidence — computed once, not per observation.** Name-similarity distance is a property of the (source name, candidate name) pair and does not change per track; it must not re-enter the running LLR sum on every sampled observation.
- **Genuinely diverse per-observation evidence (different album, different track) is treated as independent and counted at full weight — no default decay factor for repeated observations.** The only exception is the static-evidence rule above; do not apply any other blanket discount to repeated observations.
- **Correlated evidence pairs must be modeled as one joint feature, not summed from independent parts.** Specifically: alias-match + recording-match together (one compound LLR value, not the sum of two independently-weighted values), and album-match + later full-triple corroboration for the same album (the triple supersedes, per §5.2). The full list of joint-evidence overrides lives in `ScoringConfig.JointEvidencePairs` (§10.3).
- MusicBrainz's own search relevance `score` field (returned by artist/recording search, §7.2 calls C1/C4) is **never** fed into the LLR sum — it is a text-relevance ranking, not an identity-confidence signal, and measures the same underlying fact as the dedicated name-distance evidence type. Use it only as the candidate-pool admission filter (`ScoringConfig.CandidateGeneration.ArtistCandidateMinScore`).
- Composer-bucket credits must be decomposed by actual MusicBrainz relationship type, not treated as one undifferentiated "Composer matched" fact. This requires the recording lookup to request **both** work-level and recording-level relationships in one call: `inc=artist-credits+artist-rels+work-rels+work-level-rels` (§7.2, call C5). Work-level relations (`work-rels`+`work-level-rels`) surface composer/lyricist/librettist/writer; recording-level relations (`artist-rels`) surface producer/engineer/arranger — arranger specifically is a recording-level credit by MusicBrainz convention, not work-level. Record the specific relationship type found verbatim in `EvidenceRecord.RelationshipType`.

### 5.5 Sequential Sampler

The stopping rule and the sampling budget are the same mechanism — there is no separate "enough observations collected" concept apart from the running confidence crossing a bound.

```
running_llr = 0
for bucket in [AlbumArtist, Artist, Composer]:          # highest signal first
    for obs in sample_from_bucket(bucket):               # ordered by distance-seeking, §5.5.1
        running_llr += evidence_llr(obs) × role_weight(bucket)   # §6.4
        if running_llr >= AutoAcceptThreshold:
            STOP → auto-accept                            # may fire after a single observation
        if running_llr <= AutoRejectThreshold:
            STOP → auto-reject
        if observations_taken_in(bucket) >= BucketCeiling[bucket]:
            break                                          # exhausted this bucket's budget — escalate to next bucket
if no bound was crossed after all buckets/budgets exhausted:
    STOP → needs-review
```

`BucketCeiling` (default: AlbumArtist 3, Artist 4, Composer 6) is a sampling **budget**, not a target — the actual stop can occur anywhere from the first observation up to the ceiling.

#### 5.5.1 Distance-Seeking Sampling Order

Within a bucket, rank available tracks before drawing the next observation, in this priority order:
1. **Different album** over same album (avoids correlated risk from one mis-tagged release).
2. **Different track title** over repeated/similar titles (avoids weak signal from near-duplicate tracks, e.g. live/remix variants).
3. **Shorter albums (< 20 tracks)** preferred over longer ones — large-track-count releases are disproportionately compilations/box sets/deluxe editions, more likely to have incomplete or split MusicBrainz data.

These rules affect **only sampling order**, never the LLR sum directly — album length says nothing about candidate correctness, only about the likely completeness of MusicBrainz's data for that observation. Keep this out of the scoring math entirely.

### 5.6 Scoring Model

Bayesian, natural-log-odds (nats) throughout: `confidence = 1 / (1 + e^-cumulative_LLR)`, starting at 0 (50/50) per candidate. The engine tracks the top candidate's cumulative LLR and its margin over the runner-up's own cumulative LLR.

**User-facing translation layer** (required — do not expose raw LLR values in any user-facing surface): bin each evidence contribution into one of a fixed vocabulary — `Strong Positive · Positive · Weak Positive · Neutral · Weak Negative · Negative · Strong Negative` — and always pair it with a plain-language rationale sentence (e.g. "Emby says this artist has this exact track on this exact album, and MusicBrainz agrees on all three"). The LLR value drives the math; the bucket and sentence drive everything shown to a person. Raw LLR is a Diagnostic-Mode-only detail (§10.2).

### 5.7 Decision Gate

Three-way outcome, evaluated against `ScoringConfig` thresholds (default: `AutoAcceptThreshold = +4.0` nats with `MinMarginOverRunnerUp = +2.0`; `AutoRejectThreshold = -3.0` for every candidate):

- **Auto-accept**: top candidate crosses `AutoAcceptThreshold` with sufficient margin.
- **Auto-reject**: every candidate is at or below `AutoRejectThreshold` → write to `negative_cache`.
- **Needs-review**: neither condition met → write to the review queue with the full evidence trail attached.

### 5.8 Identity Cache & Propagation

On auto-accept or human-confirm, write to `identity_cache` keyed on the Emby source ItemId (not name string). Any future observation of the same source entity — including a weak, isolated Composer-only credit — first checks this cache: a match with no contradiction short-circuits straight to reuse; a contradiction reopens the case for full resolution. Periodic revalidation (not per-track) guards against staleness as MusicBrainz data changes. This is also the primary source of ongoing API-load savings once a library is substantially resolved.

### 5.9 Active Learning Feedback Loop

- Every human decision on a needs-review item is a labeled example: `(evidence bag, chosen candidate or "none")`.
- **Immediate**: store as a permanent per-entity override — do not re-ask about the same entity unless explicitly requested.
- **Periodic (batch, not per-decision)**: recompute evidence weights from the accumulated labeled set; surface recalibration suggestions rather than auto-applying them (§16).
- **Pattern flag**: if a recurring type of ambiguity appears often, surface it as a signal to add a new evidence type or generation strategy — a developer-facing signal, not an automated code change.
- Keep the labeled-decisions table (`review_decisions`) separate from `match_results` — it is training/audit data and must never be silently overwritten.

---

## 6. Evidence Catalog

All values are starting defaults, stored in `scoring_config_overrides` (§9) and editable via the Developer page (§14.3) — not hardcoded constants. Calibrate via §16 once real resolution volume exists.

### 6.1 Evidence Types and LLR Values (nats)

| Evidence | LLR | Notes |
|---|---|---|
| ProviderIds asserted MBID, confirmed | +5.0 | Tier 0, §5.1 — stronger than Tier 1 because asserted by prior tagging, but still verified, not blindly trusted |
| ProviderIds asserted MBID, confirmation failed | −4.0 | Strong negative — a stale/wrong tag is informative |
| Name similarity — near-exact | +1.5 | Static, computed once per candidate (§5.4) |
| Name similarity — close (0.85–0.95 normalized) | +0.5 | Static |
| Name similarity — poor (< 0.7) | −2.0 | Static |
| Alias match, alone | +0.3 | Weak in isolation |
| Alias + recording match (joint feature) | +1.8 | One compound value — do not sum from independent parts |
| Disambiguation/context match | +0.8 | |
| Work relationship: writer/composer/lyricist | +2.5 | Sourced from `work-rels`+`work-level-rels`, §5.4/§7.2 |
| Recording relationship: producer | +0.8 | Sourced from `artist-rels` on the recording, §5.4/§7.2 |
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
| Anchor-by-association (Strategy C) | anchor's own cumulative LLR × 0.7 | Discounted — inherited, not independently re-derived |

### 6.2 Role Weight Multipliers

Applied to whatever LLR an observation would otherwise contribute, by the role it came from:

| Role | Multiplier |
|---|---|
| AlbumArtist | 1.0× |
| Artist (performer) | 0.85× |
| Composer, undecomposed/other relationship | 0.5× |

### 6.3 Corroboration Tiers

Categorical, not continuous — encode as a specific evidence type with the tier as its value:
- **Tier 1**: Emby's Artist + Track + Album all agree with MusicBrainz. Near-decisive alone.
- **Tier 2**: Artist + Track agree, no album corroboration. Strong, needs at least one supporting signal for auto-accept.
- **Tier 3**: single field match only. Weak, background support.

### 6.4 Decision Thresholds

- `AutoAcceptThreshold`: +4.0 nats, **and** `MinMarginOverRunnerUp`: +2.0 nats over the runner-up candidate's own cumulative LLR.
- `AutoRejectThreshold`: −3.0 nats, for every remaining candidate.
- Otherwise: needs-review.

---

## 7. MusicBrainz Integration

### 7.1 Identification and Rate Policy

- `ApiPolicy.UserAgentString` (required, not optional) — an identifying User-Agent (e.g. `MetadataHealthCheck/2.0 (contact-url)`). Unidentified clients share a much less reliable, lower rate allowance.
- `ApiPolicy.MusicBrainzBaseUrl` (default `https://musicbrainz.org/ws/2/`) — configurable to point at a private proxy/mirror.
- `ApiPolicy.RateLimitEnabled` (default true) — set false when using a proxy with no rate limit. Even when disabled, retain the request queue/backoff mechanism for absorbing transient errors (timeouts, 5xx) — do not remove it, only bypass the throttle.
- `ApiPolicy.RequestsPerSecond` (default 1.0) — hard ceiling when enabled, not an aspiration; burst above it gets rejected, not gracefully queued by MusicBrainz.

### 7.2 API Call Catalog

| # | Call | Triggered By | Yields | Feeds |
|---|---|---|---|---|
| **C1** | `GET /ws/2/artist?query=artist:"{name}"&fmt=json` | Start of every resolution task | Candidate list: MBID, name, sort-name, disambiguation, type, area, life-span, MB's own text-relevance `score` | Candidate-pool admission filter only — see the score caveat in §5.4 |
| **C2** | `GET /ws/2/artist/{mbid}?inc=aliases+tags+release-groups+url-rels&fmt=json` | Once per candidate surviving C1's filter | Aliases, tags, release-groups, url-rels (external IDs) | Alias-match, tag-overlap, album-match (§5.2), external-ID evidence | Release-group/release browse endpoints **require** an explicit `type=`/`status=` filter or return empty — do not treat an unfiltered empty result as "no albums found." |
| **C3** | `GET /ws/2/recording?query=recording:"{track}"+AND+arid:{anchor_mbid}&fmt=json` | Strategy A or C | Matching recording(s), usually one: artist-credit, length, release-list | Duration filter; Tier 2/3 corroboration data included at no extra cost |
| **C4** | `GET /ws/2/recording?query=recording:"{track}"+AND+release:"{album}"&fmt=json` (fallback: drop `release:`) | Strategy B | Same shape as C3, typically more candidates | Same as C3 — same score caveat applies |
| **C5** | `GET /ws/2/recording/{id}?inc=artist-credits+artist-rels+work-rels+work-level-rels&fmt=json` | On the surviving recording candidate from C3/C4 | Work-level relations (composer/lyricist/writer) **and** recording-level relations (producer/engineer/arranger) — see §5.4 for why both `inc` terms are required in one call | Composer-relationship LLR breakdown, §6.1 |
| **C6** | `GET /ws/2/artist/{candidate_mbid}?inc=artist-rels&fmt=json` | After C5 surfaces a writer/composer MBID | Artist-to-artist relationship list | Supporting corroboration check |

### 7.3 Unverified — Check Before Building

- Exact `inc=` parameter for ISNI/IPI identifier codes — not confirmed.
- Whether any relationship-type names referenced above have since been split into more specific subtypes (MusicBrainz has done this before, e.g. with "engineer").

---

## 8. Emby Integration

### 8.1 Cost Model

This is a server-side plugin: Emby API access means the injected C# service interfaces (`ILibraryManager`, etc.), **not HTTP calls** — there is no network round-trip and no rate limit. The real cost driver is field-hydration breadth and query count (N+1 per-artist queries vs. one broad pass), not requests-per-second.

### 8.2 API Call Catalog

| # | Call | Triggered By | Yields | Feeds |
|---|---|---|---|---|
| **E1** | `ILibraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { typeof(MusicArtist).Name }, Recursive = true, DtoOptions = new DtoOptions(true) })` | Once per full library sync | Full artist list: Id, Name, sort-name | Base set for role classification |
| **E2** | `ILibraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { typeof(Audio).Name }, Recursive = true, DtoOptions = new DtoOptions(true) })` | **Once, for the whole library** — not per-artist | Every track: Name, Album, AlbumArtists, Artists, People, RunTimeTicks, ProviderIds, ParentId | Role classification, co-occurrence graph, duration checks, ProviderIds fast path — all from one query |
| **E3** | `ILibraryManager.GetItemById(Guid id)` | Targeted re-check only (content-fingerprint drift, manual re-resolution) | Single item DTO | Point verification — must not be used in a loop |
| **E4** | (field within E2's result, no separate call) | Every track, incidentally | `ProviderIds` dictionary | Tier 0 evidence, §5.1 |

**Requirement**: E2 runs as a single recursive pass over the whole library, grouped in-memory by artist afterward. Do not issue a per-artist `ArtistIds`-filtered query — everything the pre-stage and sampler need comes from the one pass. Downstream stages read from the engine's own SQLite tables (§9), never re-query `ILibraryManager`.

**Incremental sync**: run as a scheduled task (§12), following the same idiom as the reference plugin this pattern was confirmed against, rather than reacting to live library-change events.

### 8.3 Unverified — Check Before Building

- Whether a dedicated `GetArtists`/`GetAlbumArtists` convenience method exists in the current SDK version, returning role information more directly than deriving it from E2.

---

## 9. Storage

SQLite, via `SQLitePCL.pretty` (confirmed usage in the reference plugin `Emby.AutoOrganize`, not assumed). Single shared write connection guarded by a `ReaderWriterLockSlim`; read connections cloned off it. `PRAGMA journal_mode=WAL` and `PRAGMA synchronous=Normal` on initialization. Database file at `Path.Combine(appPaths.DataPath, "metadatahealthcheck-v2.db")` — entirely separate from the existing prototype's store. Schema via `CREATE TABLE IF NOT EXISTS` / `CREATE INDEX IF NOT EXISTS`; migrations via a `GetColumnNames` + `AddColumn` helper, matching the reference plugin's pattern exactly rather than introducing a separate migration framework.

### 9.1 Full Table List

```
schema_version          (version INTEGER)   -- checked/bumped in Initialize(), tracks schema (not data) migrations

source_entities         (source_system, source_type, source_id, display_name, ...)
resolution_candidates   (task_id, source_entity_id, target_system, target_type,
                         target_id, generation_strategy, generation_query, created_at)
evidence                (candidate_id, evidence_type, raw_value, role, source_track_id,
                         album_id, relationship_type, rationale)
scores                  (candidate_id, running_llr, computed_at, scoring_config_version)
match_results           (source_entity_id, target_system, target_type, target_id,
                         status[auto_accept|auto_reject|needs_review|human_confirmed|human_rejected],
                         confidence, margin, scoring_config_version, decided_at, decided_by)
review_decisions        (match_result_id, human_choice, decided_at, notes)   -- active-learning label set; never overwritten by any clear operation except an explicit, separately-confirmed one (§14.2)
negative_cache          (source_entity_id, target_system, reason, checked_at, recheck_after)
api_cache               (target_system, endpoint, query_hash, response_json, fetched_at, ttl)
identity_cache          (source_entity_id, target_system, target_id, confidence, confirmed_at, source)
artist_role_classification (artist_id, highest_role, classified_at)
artist_cooccurrence     (artist_id, cooccurring_artist_id, recording_id, role_of_cooccurring)
anchor_dependencies     (dependent_artist_id, anchor_artist_id, anchor_confidence_at_use, resolved_at)
content_fingerprints    (item_id, fingerprint_hash, item_type, computed_at)   -- item-id drift handling, §9.2
scoring_config_overrides (key, value, updated_at)   -- §10.3, editable tuning values; also holds "suggested_" prefixed rows from the calibration job, §16
```

`api_cache` and `negative_cache` mean a re-run only queries what's new or stale — no raw MusicBrainz response should ever be fetched twice for the same query.

### 9.2 Item ID Drift Handling

Primary key for tracked entities is the Emby ItemId. Alongside it, store a content fingerprint (normalized title + album + duration, hashed, for tracks; name + earliest-seen-release, for artists) in `content_fingerprints`. On each sync, if a previously-known ItemId is missing, search recently-added unmapped items for a fingerprint match before treating it as new — if found, carry over the resolution history to the new ItemId and log the carry-over explicitly.

---

## 10. Configuration

Three separate objects, on two separate pages (§13):

### 10.1 EngineConfig (main user-facing page)

```
EngineConfig
├── CandidateGeneration.StrategyC.Enabled: bool
├── ApiPolicy
│   ├── MusicBrainzBaseUrl: string (default "https://musicbrainz.org/ws/2/")
│   ├── RateLimitEnabled: bool (default true)
│   ├── RequestsPerSecond: float (default 1.0, ignored if RateLimitEnabled=false)
│   ├── UserAgentString: string (required)
│   └── CacheTtlByEndpoint[], NegativeCacheRecheckInterval
├── Anchoring
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

Applied inside `EmbyArtistProvider.GetAll()` (§11.3) — if `ArtistFilter` is non-empty, return only matching entities. No other pipeline component needs to know a filter is active.

### 10.3 ScoringConfig (developer page, grid-editable — §14.3)

Row-shaped tuning data, stored as key-value rows in `scoring_config_overrides` (§9.1), not a serialized blob — every value from §6 is one row, defaulting to the values listed there if absent. Every save increments `scoring_config_version`, and every `match_result`/`score` row stamps the version active at decision time, so past decisions remain attributable to the weights that produced them even after further tuning.

```
ScoringConfig
├── CandidateGeneration.StrategyA/B.MaxCandidates, DurationToleranceSeconds, ArtistCandidateMinScore
├── BucketCeiling { AlbumArtist: 3, Artist: 4, Composer: 6 }
├── ComputeStaticEvidenceOncePerCandidate: bool
├── PreferDifferentAlbum, PreferDifferentTrackTitle: bool
├── PreferAlbumTrackCountBelow: int (default 20)
├── AutoAcceptThreshold, AutoRejectThreshold, MinMarginOverRunnerUp
├── EvidenceWeights[]   -- one row per §6.1 evidence type
├── RoleWeights { AlbumArtist, Artist, Composer }
├── ComposerRelationshipWeights { writer, producer, arranger, other }
├── AlbumMatchWeights { distinctiveTitle, genericTitle }
└── JointEvidencePairs[]   -- correlated-pair overrides, §5.4
```

---

## 11. Class Structure

### 11.1 Folder / Namespace Layout

All new code under `MetadataHealthCheck.v2`, entirely separate from the existing prototype.

```
MetadataHealthCheck.v2/
│
├── Core/                                 -- generic; no knowledge of Emby, MusicBrainz, Artist, or Album
│   ├── Model/                            -- §4
│   ├── Interfaces/                       -- §11.2
│   └── Engine/
│       ├── ResolutionEngine.cs           -- orchestrates §5's pipeline
│       └── SequentialSampler.cs          -- §5.5
│
├── Tasks/
│   ├── MetadataHealthCheckSyncTask.cs    -- IScheduledTask, §12
│   └── CalibrationBacktestTask.cs        -- IScheduledTask, §16
│
├── Api/
│   └── DeveloperToolsService.cs          -- ServiceStack routes, §14.2
│
├── Config/
│   ├── EngineConfig.cs                   -- §10.1, EditableOptionsBase (BasePluginSimpleUI)
│   ├── DeveloperConfig.cs                -- §10.2
│   └── WebPages/
│       └── metadatahealthcheck-developer.html / .js   -- §13.2; main page needs no embedded resource (§13.1)
│
├── Sources/
│   └── Emby/
│       ├── EmbyArtist.cs                 -- ISourceEntity
│       ├── EmbyArtistProvider.cs         -- ISourceEntityProvider<EmbyArtist>, applies DeveloperConfig.ArtistFilter
│       ├── EmbyLibraryReader.cs          -- the E2 fat query, §8.2
│       ├── EmbyArtistTieredOrdering.cs   -- IProcessingOrderStrategy<EmbyArtist>, §5's wavefront
│       └── EmbyCooccurrenceGraphBuilder.cs
│
├── Resolvers/
│   └── MusicBrainz/
│       ├── MusicBrainzArtistResolverPlugin.cs   -- IResolverPlugin<EmbyArtist>
│       ├── Client/
│       │   ├── MusicBrainzApiClient.cs
│       │   └── MusicBrainzRateLimiter.cs
│       ├── Strategies/
│       │   ├── AnchoredRecordingStrategy.cs      -- Strategy A, C3
│       │   ├── SoftBucketStrategy.cs             -- Strategy B, C4
│       │   ├── AnchorByAssociationStrategy.cs    -- Strategy C
│       │   └── ProviderIdFastPathStrategy.cs     -- §5.1
│       └── Evidence/
│           ├── NameDistanceEvidenceCollector.cs
│           ├── AliasEvidenceCollector.cs
│           ├── WorkRelationshipEvidenceCollector.cs
│           ├── RecordingRelationshipEvidenceCollector.cs
│           ├── AlbumMatchEvidenceCollector.cs
│           └── CorroborationTierEvidenceCollector.cs
│
├── Scoring/
│   ├── BayesianBeliefScorer.cs           -- IBeliefScorer, §5.6
│   ├── EvidenceTranslator.cs             -- LLR → plain-language bucket, §5.6
│   └── JointEvidenceRules.cs             -- §5.4
│
├── Storage/
│   └── Sqlite/
│       ├── BaseSqliteRepository.cs       -- §9's idiom
│       ├── MatchRepository.cs            -- IMatchRepository
│       ├── IdentityCacheRepository.cs    -- IIdentityCache
│       ├── AnchorDependencyRepository.cs
│       └── ApiCacheRepository.cs
│
├── ActiveLearning/
│   ├── ReviewQueueService.cs
│   ├── CalibrationBacktestJob.cs         -- §16, invoked by Tasks/CalibrationBacktestTask.cs
│   └── LabeledDecisionStore.cs
│
└── Diagnostics/
    └── StructuredLogger.cs               -- §15
```

### 11.2 Core Interfaces

None of these mention Emby, MusicBrainz, Artist, or Album by name — that constraint is what makes §11.4's extensibility requirement checkable rather than aspirational.

```csharp
namespace MetadataHealthCheck.v2.Core.Interfaces
{
    public interface ISourceEntityProvider<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        IEnumerable<TSourceEntity> GetAll(ResolutionContext context);
    }

    // Optional. A source entity type with no natural processing order returns entities unchanged.
    public interface IProcessingOrderStrategy<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        IEnumerable<TSourceEntity> OrderForProcessing(IEnumerable<TSourceEntity> all);
    }

    public interface ICandidateGenerationStrategy<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        string StrategyName { get; }
        int Priority { get; }
        IEnumerable<Candidate> GenerateCandidates(TSourceEntity source, ResolutionContext context);
    }

    public interface IEvidenceCollector<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        string EvidenceType { get; }
        EvidenceRecord Collect(TSourceEntity source, Candidate candidate, ResolutionContext context);
    }

    public interface IBeliefScorer
    {
        ScoredCandidate Score(Candidate candidate, IEnumerable<EvidenceRecord> evidenceSoFar);
    }

    public interface IDecisionGate
    {
        MatchResult Decide(IEnumerable<ScoredCandidate> rankedCandidates, ScoringConfig config);
    }

    // The unit of extensibility for new target systems/entity types: one implementation
    // per (target system, target entity type) pair.
    public interface IResolverPlugin<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        string TargetSystem { get; }
        string TargetEntityType { get; }
        IEnumerable<ICandidateGenerationStrategy<TSourceEntity>> Strategies { get; }
        IEnumerable<IEvidenceCollector<TSourceEntity>> EvidenceCollectors { get; }
        IBeliefScorer Scorer { get; }
        IDecisionGate DecisionGate { get; }
    }

    public interface IIdentityCache
    {
        MatchResult? Get(string sourceSystem, string sourceId, string targetSystem);
        void Set(string sourceSystem, string sourceId, string targetSystem, string targetId, double confidence);
    }

    public interface IMatchRepository
    {
        void SaveCandidate(Candidate candidate);
        void SaveEvidence(EvidenceRecord evidence);
        void SaveMatchResult(MatchResult result);
        MatchResult? GetExisting(string sourceSystem, string sourceId, string targetSystem);
    }
}
```

### 11.3 Requirement: Composer/Album Filter Application Point

`DeveloperConfig.ArtistFilter`/`AlbumFilter` (§10.2) is applied **only** inside the `ISourceEntityProvider<TSourceEntity>` implementation for the relevant source type — no other layer inspects or is aware of it.

### 11.4 Extensibility Requirement

Adding a new target system or source entity type must require **zero changes** to `Core/`, `Storage/`, or the engine orchestration. This is a testable requirement, not a design aspiration — verify it by implementing the two cases below before considering the architecture validated:

- **New target system (e.g. Discogs), same entity type**: new `Resolvers/Discogs/` folder, `DiscogsArtistResolverPlugin : IResolverPlugin<EmbyArtist>` with its own strategies/evidence collectors. `match_results.target_system` gains a new value; no schema migration required.
- **New source entity type (e.g. Album), same target system**: new `EmbyAlbum : ISourceEntity` and `EmbyAlbumProvider : ISourceEntityProvider<EmbyAlbum>`, plus `MusicBrainzAlbumResolverPlugin : IResolverPlugin<EmbyAlbum>` with its own evidence collectors (e.g. track-listing overlap instead of work/writer relationships). `EmbyAlbumProvider` simply omits an `IProcessingOrderStrategy` implementation, since Album has no "role" concept — this is valid because that interface is optional, not assumed.

---

## 12. Task Invocation & Composition Root

### 12.1 Concurrency

**None.** The entire pipeline — sync task, tiered wavefront, per-entity resolution, sequential sampler — runs strictly sequentially: one artist and one observation at a time. No parallel evidence collection, no parallel candidate scoring, no concurrent entity processing.

### 12.2 Scheduled Tasks

`IScheduledTask` is the invocation mechanism (confirmed against the reference plugin `Emby.AutoOrganize`, not assumed):

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

Registering this gets a scheduled/recurring run **and** a manual "Run Now" trigger for free from Emby's own admin dashboard — no custom trigger UI is required. `CalibrationBacktestTask` registers as a second, independent `IScheduledTask`, since it never touches the network and can run on its own schedule.

### 12.3 Composition Root

Confirmed pattern, directly from the reference plugin's `PluginEntryPoint.cs`:

- Implement `IServerEntryPoint` (e.g. `EngineEntryPoint.cs`). Its constructor is auto-injected by Emby's container with Emby's own native services (`ILibraryManager`, `ITaskManager`, `ILogger`, `IServerConfigurationManager`, etc.).
- Inside `Run()`, manually construct the engine's own services in dependency order: `MatchRepository` (needs `IServerConfigurationManager.ApplicationPaths` for the db path) → `MusicBrainzApiClient` → each `IResolverPlugin` implementation → `ResolutionEngine`. There is no generic custom-service-registration mechanism in this SDK generation — manual construction is the idiomatic pattern here, not a workaround.
- Expose the constructed `ResolutionEngine` via a static `EngineEntryPoint.Current` singleton. `MetadataHealthCheckSyncTask` (itself Emby-native-DI-injected) and `DeveloperToolsService` (ServiceStack-resolved) both reach it this way, since neither gets it constructor-injected directly.

---

## 13. Config Pages

Two distinct, real Emby mechanisms — confirmed against Emby's own SDK reference docs, not assumed to be interchangeable.

### 13.1 Main Page — `BasePluginSimpleUI<EngineConfig>`

`EngineConfig : EditableOptionsBase` (§10.1). Implementing `Plugin : BasePluginSimpleUI<EngineConfig>` auto-generates the settings form from the class's properties — no HTML/JS required for this page.

**Explicit limitation, stated in Emby's own docs**: this mechanism does not support custom button actions or multi-page complexity. This is *why* it is used only for `EngineConfig` (plain fields, no actions) and not for the Developer page.

### 13.2 Developer Page — Hand-Built HTML/JS (`IHasWebPages`)

Required because of §13.1's stated limitation — the Developer page needs button actions (§14) and grid-shaped data (§14.3), neither supported by SimpleUI. Uses the confirmed-working `IHasWebPages`/`PluginPageInfo` pattern (from the reference plugin), reached via a link from the main page rather than `EnableInMainMenu`.

**Unverified — check before building**: whether one plugin class can cleanly implement both `BasePluginSimpleUI<EngineConfig>` (for §13.1) and `IHasWebPages` (for this page) simultaneously, since the former's base class (`BasePlugin`) does not carry the classic `BasePlugin<TConfig>.Configuration` mechanism the hand-built page's JS would use for `DeveloperConfig`. **Confirmed-safe fallback if this doesn't compose cleanly**: both pages as hand-built `IHasWebPages` HTML/JS under one `BasePlugin<CombinedConfig>`, where `CombinedConfig` nests `EngineConfig` and `DeveloperConfig` — loses the auto-generated form for the main page, but has zero unverified assumptions.

### 13.3 Developer Page Contents

- `DeveloperConfig` fields (§10.2): `ArtistFilter`, `AlbumFilter` text inputs, `DiagnosticMode` and related toggles.
- Granular clear-operation buttons (§14).
- Scoring/weighting tuning grid (§14.3).

---

## 14. Developer Tools

### 14.1 Route

One flexible endpoint covers all clear operations and the scope modifier — do not create one route per operation.

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

Modeled directly on a confirmed real precedent in the reference plugin (`ClearOrganizationLog`, a `DELETE` route).

### 14.2 Operations

Ordered from cheapest/most-preserving to most-destructive. Each is a distinct button on the Developer page, not a raw text field for `What`.

| Operation | Clears | Keeps | Purpose |
|---|---|---|---|
| **Re-score** | `scores`, `match_results` | `resolution_candidates`, `evidence` | Tuning LLR values/thresholds — zero network calls, pure recomputation. The primary operation, paired with the scoring grid's Save button (§14.3). |
| **Re-resolve from cache** | + `resolution_candidates`, `evidence` | `api_cache` | Candidate-generation or evidence-collection code changed — re-runs logic, serves from cache wherever possible |
| **Clear API cache** | `api_cache` | Everything else | Suspected stale MB data |
| **Clear negative cache** | `negative_cache` | Everything else | Retry previously-rejected entities |
| **Clear identity cache** | `identity_cache` | Everything else | Test propagation/short-circuit logic (§5.8) in isolation |
| **Clear anchor dependencies** | `anchor_dependencies` | Everything else | Test cascade-reopen logic in isolation |
| **Clear review decisions** | `review_decisions` | Everything else | **Always separate, always explicitly confirmed in the UI** — this is the active-learning ground truth; never bundled into any other operation |
| **Full wipe** | Everything, including review decisions | Nothing | True start-from-zero |

**Scope modifier**: every operation above can run against the whole library or be scoped to `DeveloperConfig.ArtistFilter`'s current value — reuse the same parsing (§10.2). Scoped "re-score" on a single artist is the primary interactive-development loop this tooling exists for.

### 14.3 Scoring/Weighting Tuning Grid

Plain HTML tables with number inputs are sufficient — no grid library needed for this specific case (reserve a real grid component for something with genuinely more rows/complexity, like a future review-queue browser). Sections, each editable:

- Evidence LLR values (§6.1) — one row per evidence type.
- Role weights (§6.2) — three rows.
- Decision thresholds (§6.4).
- Sampling — bucket ceilings, the three distance-seeking toggles (§5.5.1), `PreferAlbumTrackCountBelow`.
- Joint evidence overrides (§5.4) — shown as pairs with their combined value, not as independently-multiplied components.

**Two required actions**:
- **"Save & Re-score"** — persists edits (bumping `scoring_config_version`), then immediately triggers the Re-score operation (§14.2), scoped to `ArtistFilter` if set. One click for the tune-then-see-result loop.
- **"Reset to defaults"** — reverts `scoring_config_overrides` to §6's built-in starting values.

**Calibration integration**: when the backtest job (§16) determines a value is mis-calibrated, it writes a `suggested_`-prefixed row rather than overwriting the live value. The grid shows a "suggested" column wherever the backtest has an opinion, with a per-row "accept suggestion" action.

---

## 15. Logging

### 15.1 Logger

Confirmed real interface (from the reference plugin, not assumed): `ILogger` exposes `Debug`, `Info`, `Warn`, `ErrorException(message, exception, args)` — indexed `{0}` placeholders, no separate `Trace` level. One logger instance, scoped to the name **`MetadataHealthCheck`**, not per-class loggers with different root names.

**Unverified — check before building**: the exact call to obtain a logger scoped by name; not found in the reference plugin's source (likely wired elsewhere in DI registration).

### 15.2 Principle

Log by default; filter by level, not by omission. Any function whose outcome matters to a power user, an admin, or a developer gets a log line at the appropriate level — including one-off diagnostic probes, which use the same permanent logger, never a throwaway console channel.

### 15.3 Level ↔ Audience

| Level | Audience | Content |
|---|---|---|
| `Info` | Power user | One line per resolved entity — outcome only |
| `Info` | Admin | Lifecycle events — run start/end, counts, durations |
| `Warn` | Admin | Recoverable anomalies — rate-limit backoff, cascaded re-opens |
| `ErrorException` | Admin/Developer | Failed calls, exceptions |
| `Debug` | Developer | Per-candidate detail — strategy attempts, evidence LLR contributions, sampler running total, decision math. Raw request/response payloads gated behind `DiagnosticMode`/`RetainRawApiResponses` (§10.2) since no `Trace` level exists. |

### 15.4 Component Prefix

Every line carries a `[ComponentName]` prefix, mapped 1:1 to §11.1's folder structure — one prefix per logical component family:

`[Sync] [Engine] [Sampler] [CandidateGen] [Evidence] [Scorer] [DecisionGate] [MBClient] [EmbyReader] [IdentityCache] [Anchor] [ActiveLearning] [Calibration] [Config]`

### 15.5 Structured Log = Same Event, Two Outputs

`Diagnostics/StructuredLogger.cs` is the single call site: it writes the structured evidence record to SQLite (§9, feeding the future review UI and the calibration job) **and** emits the human-readable `[Component] ...` line to `ILogger` at the matching level, from one call — preventing the narrative log and the stored evidence trail from silently drifting apart.

---

## 16. Calibration (Self-Critique)

Runs as `CalibrationBacktestTask` (§12.2), against cached evidence only — no network calls.

- **Calibration check**: group all decisions by their confidence at decision time into buckets (e.g. 90–95%, 95–100%). For each bucket, check what fraction were actually correct once known (auto-accepted-and-never-overridden, or human-confirmed). A bucket that's overconfident (e.g. the 90–95% bucket is only right 70% of the time) signals the relevant evidence weight should be lowered; underconfident signals a threshold could safely be lowered.
- **Sampling-sufficiency check**: for a sample of auto-accepted matches, replay the *other* candidate-generation strategy or a wider ceiling against cached data. If it would have surfaced a better candidate often, the ceiling is too aggressive (under-sampling). If a narrower ceiling would have found the same winner just as reliably, the ceiling can be lowered (over-sampling, wasted API budget in the original run).
- **Anchor-trust check**: what fraction of anchor-dependent resolutions (Strategy C) had their anchor later overturned — a direct measure of whether cross-artist anchoring is trustworthy in practice.
- Output is a **report and suggested `scoring_config_overrides` values** (§14.3) — never an automatic change to live weights/thresholds.

---

## 17. Testing Strategy

Two distinct layers — do not conflate them.

### 17.1 Unit Tests (xUnit — default; swap if you have a strong reason)

Fast, deterministic, no network, no live Emby dependency. Cover:
- Scorer LLR combination math against known evidence bags (§18.1's worked example should be a literal test case).
- Sequential sampler stopping behavior against synthetic observation sequences.
- Evidence collectors' parsing of **fixture MusicBrainz JSON responses** (real recorded responses saved to disk, replayed by a test-mode `IMusicBrainzApiClient` implementation — no live network calls in CI, no rate-limit concerns, and a regression in the §5.4 relationship-decomposition requirement gets caught automatically).
- `DeveloperConfig` CSV-filter parsing (§10.2).
- Repository CRUD and migration correctness against a temporary SQLite file per test.

### 17.2 Real-World Validation (not a unit test)

The §19 comparison run and the §16 calibration job. No fixture-based test can establish that the engine is *right* about the real world, only that it's internally consistent with its own rules — this layer exists specifically to answer the question unit tests cannot.

---

## 18. Worked Example (Validation Reference)

One concrete trace, for validating an implementation matches intent — this should become an actual test case per §17.1.

**Setup**: Emby Artist "Sarah Vaughan" (AlbumArtist tier), two candidates survive C1/C2: MBID `X` (correct) and MBID `Y` (a different, less-famous same-named artist).

```
Candidate X:
  Album-match precursor: distinctive album title matches         llr += 1.5   → running = 1.5
  Observation 1 (AlbumArtist bucket, album "Crazy and Mixed Up"):
      Tier 1 full triple (Artist+Track+Album all agree)          llr += 3.5   (supersedes the 1.5 precursor for this album, §5.2)
      Name similarity, static, computed once                      llr += 1.5
  running_llr = 5.0

Candidate Y (same observations checked against it):
  Album-match precursor: no title overlap                          llr += 0
  Observation 1: no track/album match against Y                    llr -= 2.0
  running_llr = -2.0

Margin = 5.0 - (-2.0) = 7.0 ≥ MinMarginOverRunnerUp (+2.0)
```

**Expected result**: auto-accept X after **one observation** — `BucketCeiling.AlbumArtist` (3) is never needed here; it's the ceiling this run stayed well under. This is the sequential-sampler behavior from §5.5, not a fixed-batch result.

---

## 19. Deployment & Cutover

### 19.1 Side-by-Side Requirement

v2 runs against its own SQLite database, its own configuration, and its own scheduled task — no shared state with the existing prototype, no modification to the prototype's data. Both can run against the same library simultaneously for comparison.

### 19.2 Cutover Acceptance Criteria

Both of the following, not either alone:

1. **Zero regressions on a curated golden-set** of artists, deliberately including hard cases (composers, same-named-different-people, tribute-band-alikes), against known-correct answers.
2. **Manual trial-period sign-off** — not a fixed statistical bar. Confirm two things directly: the engine is genuinely picking the right winners (not merely self-consistently confident), and the evidence trail (§5.6's plain-language buckets) for both auto-accepts and needs-review items is legible enough to understand *why* a decision was made.

---

## 20. Assumptions to Verify Before/During Build

Consolidated from throughout this document — none of these block starting the build, but each should be confirmed early rather than discovered late:

1. Exact `inc=` parameter for MusicBrainz ISNI/IPI codes (§7.3).
2. Whether MusicBrainz relationship-type names used here have since been split further (§7.3).
3. Whether a dedicated `GetArtists`/`GetAlbumArtists` Emby SDK method exists (§8.3).
4. The exact call to obtain a logger scoped by name (§15.1).
5. Whether `BasePluginSimpleUI<T>` and `IHasWebPages` can be implemented on the same plugin class simultaneously (§13.2) — confirmed-safe fallback provided if not.

---

## 21. Phased Build Plan

Building the entire spec simultaneously is not a realistic first milestone. Build in this order; each phase is independently testable.

1. **Skeleton + one path end-to-end**: `Core` model/interfaces (§4, §11.2), SQLite storage (§9, core tables only), `Sources/Emby` (§8), `Resolvers/MusicBrainz` with **only Strategy A/B** and **only two evidence types** (name similarity, work-relationship). A simple weighted-sum scorer is acceptable here — Bayesian comes in phase 2. Goal: one artist resolved end-to-end, logged, stored.
2. **Full evidence set + Bayesian scoring**: remaining evidence collectors (§6), switch to `BayesianBeliefScorer`, add the sequential sampler (§5.5) and decision-gate margins. Goal: reproduce §18's worked example exactly.
3. **Tiering + anchoring**: role classification, co-occurrence graph, Strategy C (§5.3, §9). Goal: composer-only artists resolving via borrowed anchors.
4. **Active learning + calibration**: review queue, identity cache, calibration backtest job (§5.9, §16).
5. **Developer tooling + config pages**: `EngineConfig` SimpleUI page, Developer HTML/JS page with filters/granular clears/scoring grid (§13–14). Deliberately last — useful throughout development as plain hardcoded test values in the meantime, but not blocking earlier phases.
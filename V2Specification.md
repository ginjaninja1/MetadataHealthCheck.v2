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
  │  0. Per-Artist Track Read (§8.2, call E2) — full, fresh read of   │
  │     this artist's tracks, done now, not upfront. Supersedes the   │
  │     provisional tier with an authoritative role classification;   │
  │     buckets tracks into AlbumArtist/Artist/Composer for the        │
  │     sampler (§5.5.1); co-occurring artists on the same recordings │
  │     are discovered incidentally and upserted into                 │
  │     artist_cooccurrence (§9) as they're seen — there is no global  │
  │     co-occurrence pre-pass.                                        │
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

**Confirmed direction (settled 2026-07-12, following a design review against real MusicBrainz data — see Project Log Directives and Evidence Log for the full reasoning): candidate generation is artist-search-first, not recording-search-first.** The previous recording-search-first design (still what `SoftBucketStrategy.cs` implements as of this writing — the code has not yet been updated to match this section) is preserved in **Appendix A** for reference, in case a fundamental flaw in this direction is found later. It should not be treated as a live alternative unless that happens.

Three strategies, tried in this priority order:

- **Strategy A (own anchor)**: if the source artist already has a confirmed MBID anchor, query `recording:"{track}" AND arid:{anchor_mbid}` (§7.2, call C3). Near-certain single hit. Even a single hit is passed through full evidence scoring — an anchor does not bypass verification. **Unaffected by this change** — an anchor already identifies a specific artist by ID, so there's no name-search step to invert here.
- **Strategy C (borrowed anchor — Composer tier only)**: if no own anchor exists, but a co-occurring artist on the same recording (from the `artist_cooccurrence` table, §9) is already resolved, use *that* artist's confirmed MBID as the anchor for the same query shape as Strategy A. Record the anchor dependency (§9, `anchor_dependencies` table) with the anchor's confidence at time of use, so an overturned anchor can trigger a cascade re-open. **Unaffected by this change**, for the same reason as Strategy A.

  **Parked extension, not built (confirmed 2026-07-12):** the co-occurring artist used as the anchor is currently identified from `artist_cooccurrence` however that table keys its rows today. A more precise version of this same idea — noted here so it isn't lost, not because it's being built now — would require each track *observation* (not just the candidate being evidenced) to carry the **Emby artist entity ID** of the co-occurring performer, not merely a name string, since names collide and a name-keyed anchor can silently borrow the wrong artist's identity. This is real but judged fringe-benefit relative to its cost while the engine's basics are still being built; revisit once Strategy C sees real usage and the `IObservationUnit` interfaces (§11.2) can be extended to carry entity IDs, not before.

- **Strategy B (artist-search-first, confirmed direction), in two distinct stages:**

  **Stage 1 — Admission gate (candidate generation, one-time per artist, contributes zero evidence).** Query `artist:"{source artist name}"` (§7.2, call C1) to get a pool of candidate artists **and their registered aliases** in one call. For each candidate, normalize both the Emby-tagged name and the candidate's primary name and every alias using a configurable replacement table (§10.3 `NameNormalizationRules` — strip leading "The", fold `&`/`and`/`+`, strip apostrophes, strip `feat`/`featuring`/`vs`/`with` credit suffixes, strip punctuation, fold case, collapse whitespace, normalize diacritics), then compute Levenshtein distance. Name and every alias are weighted **equally** at this stage — the question is only "does this MBID deserve a `/recording` lookup at all," not how good a match it is. Admit if the best distance across name-or-any-alias clears `ScoringConfig.CandidateGeneration.ArtistCandidateMaxEditDistance`. **This gate contributes nothing to the LLR sum** — it only decides which MBIDs proceed to Stage 2.

  `ScoringConfig.CandidateGeneration.ArtistCandidateMinScore` (MusicBrainz's own returned Lucene relevance `score`, 0–100, confirmed present in the real C1 response) remains a distinct, separate config value, defaulted to **0** (a no-op today, since it is not yet established as a reliable admission signal on its own). It is retained rather than removed in case future tuning against real resolution volume shows it adds value, alone or in combination with the edit-distance gate — see §10.3.

  **Stage 2 — Recording-level corroboration (per track, repeatable, real evidence weight).** For each admitted candidate, confirm via the same per-track recording lookup already built for evidence collection (`RecordingLookup`, §5.4). When a `/recording` result's artist-credit text is compared against the candidate, a match against the candidate's **primary name** counts at full weight (`ScoringConfig.NameMatchWeight`, default 1.0); a match only via one of the candidate's **aliases** is genuine but slightly weaker corroboration (`ScoringConfig.AliasMatchWeight`, default 0.9, tunable — an alias match is "another bite at the cherry to find a match in the data," not a claim that alias-vs-name disambiguates identity). This factor multiplies whatever Corroboration Tier value (§6.3) that track observation would otherwise contribute. See §6.3 and §5.4 for exactly where this multiplier applies.

All strategies return candidates tagged with generation provenance (strategy name, literal query) for logging (§15) and evidence traceability.

**Known trade-off of this confirmed direction, accepted deliberately rather than solved (same review):** admitting candidates by name/alias match first risks *excluding* a correct candidate upfront if its real MusicBrainz artist-credit text doesn't resemble the Emby-tagged name and isn't in MusicBrainz's own registered alias list (a genuine MB data-completeness gap, not hypothetical) — the mirror-image risk of the old design's tendency to admit spurious candidates that then had to be cancelled out after the fact by negative name-similarity. Accepted because a ground-truth Emby observation gives a real artist name to search against, and the responsibility for that ground truth being reasonably accurate sits with the user/library tagging, not the engine. The Stage 1 threshold is deliberately allowed to be looser than perfect — a marginal name match can still be admitted and let Stage 2's real track evidence decide it, rather than being excluded outright at the gate.

### 5.4 Evidence Collection Rules

- Every evidence collector implements `IEvidenceCollector<TSourceEntity>` (§11.2) and returns an `EvidenceRecord` with the **raw observed fact**, never a pre-baked LLR value — the LLR is looked up from the current `ScoringConfig` (§10.3) at scoring time. This is required for the "re-score without re-fetching" developer operation (§14.2) to work.
- **Name/alias comparison is not itself an evidence type — it is a two-stage mechanism spanning candidate generation and corroboration weighting (confirmed 2026-07-12, superseding all prior framing of "name similarity" as a scored fact; see §6.1 for what this replaced).** Stage 1 (§5.3, admission gate) is a one-time, per-artist, binary pass/fail with zero LLR contribution. Stage 2 (§5.3, corroboration) applies per recording-lookup result, as a multiplier (`NameMatchWeight`/`AliasMatchWeight`, §10.3) on whatever Corroboration Tier value (§6.3) that lookup would otherwise contribute — it is not a separate additive line, and is not computed once-and-cached the way the old static model assumed. Two candidates with genuinely identical track-level evidence and identical match-type on every track are a correct needs-review tie, not a gap requiring a manual tie-break rule.
- **Genuinely diverse per-observation evidence (different album, different track) is treated as independent and counted at full weight — no default decay factor for repeated observations.**
- **The album-match + later full-triple corroboration correlated pair remains a joint-feature case** — a full Tier 1 triple for a given album supersedes the standalone album-match precursor's contribution for that same album (§5.2), rather than summing both. **Implementation note (confirmed during Phase 2 planning):** this specifically can only be resolved at scoring time, not collection time — the album-match precursor (§5.2) always runs once, before any per-track observation exists, so it has no way to know in advance whether a later observation will produce a full-triple for the same album. `SimpleWeightedSumScorer` implements this one case directly as a narrow, explicit filter, ahead of the general `JointEvidencePairs` mechanism (`JointEvidenceRules.cs`), which remains unbuilt for any other case. (The alias+recording-match joint pair that previously also lived under this rule is retired along with the rest of old §6.1's static name-similarity model — see above.)
- **Per-track recording lookups use a fallback ladder (confirmed during Phase 2 planning, not part of the original design):** trackname+artistname+albumname → trackname+albumname → trackname alone, tried in that order until a hit for the candidate being evidenced is found. Each rung risks its own false-positive shape (a wrong album hurts the tightest rung; a wrong artist-name-text hurts the middle rung) — there is no way to design around this tension when falling back to a looser query at all, so it is accepted as a known, low-probability-in-practice risk rather than solved, relying on `NameDistanceEvidenceCollector`'s rejection of poor matches (too distant to trust at all, regardless of rung) as the real safety net. This same collector is also the one that determines, per recording result, whether the match was against the candidate's primary name or an alias (`MatchedViaAlias: true/false`) — one collector doing double duty: reject if too poor to trust, otherwise report which weight (§5.3 Stage 2) the scorer should apply. Every lookup records which rung produced its result (diagnostic-only for now, not yet used to change behavior) — intended to eventually feed §16's calibration job with the data needed to answer "is it ever worth falling back to trackname-alone, or never?" Implemented once, shared, and memoized per (candidate, track) pair (`Resolvers/MusicBrainz/Evidence/RecordingLookup.cs`) rather than re-implemented per collector, since `WorkRelationshipEvidenceCollector`, `RecordingRelationshipEvidenceCollector`, and `CorroborationTierEvidenceCollector` all need the same underlying lookup for the same fact.
- MusicBrainz's own search relevance `score` field (returned by artist/recording search, §7.2 calls C1/C4) is confirmed present in real responses (0–100, Lucene-based) but is **never** fed into the LLR sum. It is not currently used for the Stage 1 admission gate either (that's edit-distance based, §5.3) — it's retained as `ScoringConfig.CandidateGeneration.ArtistCandidateMinScore`, defaulted to 0 (inert), reserved in case future tuning shows it has standalone or combined value.
- Composer-bucket credits must be decomposed by actual MusicBrainz relationship type, not treated as one undifferentiated "Composer matched" fact. This requires the recording lookup to request **both** work-level and recording-level relationships in one call: `inc=artist-credits+artist-rels+work-rels+work-level-rels` (§7.2, call C5). Work-level relations (`work-rels`+`work-level-rels`) surface composer/lyricist/librettist/writer; recording-level relations (`artist-rels`) surface producer/engineer/arranger — arranger specifically is a recording-level credit by MusicBrainz convention, not work-level. Record the specific relationship type found verbatim in `EvidenceRecord.RelationshipType`.

### 5.5 Track Observation Feeder and Sequential Resolution Engine

**Terminology split (confirmed 2026-07-12 — these were previously nested under one "Sequential Sampler" heading, which blurred two distinct components):** the **Track Observation Feeder** (§5.5.1) is a pre-engine step that selects and orders which Emby *track* to observe next for the artist under inspection. The **Sequential Resolution Engine** (below) is the separate downstream loop that takes each fed observation, generates/confirms candidates against it, accumulates evidence, and decides when to stop. The feeder knows nothing about candidates or LLR; the engine knows nothing about track selection. Each observation the feeder produces carries its bucket tier (AlbumArtist/Artist/Composer) along with it into the engine, since the tier affects both the role-weight multiplier (§6.2) and which generation strategies are even in play (Strategy C, §5.3, is Composer-tier only).

The stopping rule and the sampling budget are the same mechanism — there is no separate "enough observations collected" concept apart from the running confidence crossing a bound.

```
running_llr = 0
for bucket in [AlbumArtist, Artist, Composer]:          # highest signal first
    for obs in feed_from_bucket(bucket):                 # ordered by the Feeder, §5.5.1
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

`BucketCeiling` (default: AlbumArtist 3, Artist 4, Composer 6) is a sampling **budget**, not a target — the actual stop can occur anywhere from the first observation up to the ceiling. **Open item, deliberately deferred (confirmed 2026-07-12):** exactly when the engine should give up on finding a better-supported candidate versus asking the Feeder for one more observation is expected to need real tuning once resolution volume exists — no change proposed here beyond what `BucketCeiling` already provides as a first-pass budget.

**Clarification (confirmed during Phase 2 planning — the pseudocode above reads ambiguously on this point):** the loop above is written per-candidate for readability, but the engine evaluates every live candidate **jointly, one round at a time** — not one candidate's loop run to completion in isolation before moving to the next. This is required by §5.7's margin check, which compares the top candidate's cumulative LLR against the runner-up's own cumulative LLR: that comparison is only meaningful if every live candidate has been given the same observations at the same point. Concretely: draw one observation, score it against *every* live candidate, check the decision gate (top crosses threshold *and* margin over runner-up, or all candidates at/below reject), and only then draw the next observation if neither condition is met. §18's worked example is written this way for exactly this reason — the same observation is checked against both X and Y before the margin is evaluated.

#### 5.5.1 Track Observation Feeder — Distance-Seeking Order

Within a bucket, rank available tracks before feeding the next observation to the engine, in this priority order:
1. **Different album** over same album (avoids correlated risk from one mis-tagged release).
2. **Single-credit tracks** (exactly one `Artist` and one `AlbumArtist`) over multi-credit tracks — an unambiguous credit is more likely to produce a clean, undisambiguated MusicBrainz match on the other side of the lookup, making Tier 1 full-triple corroboration (§6.3) more likely to land per observation. This is an ordering preference only: a multi-credit track is not discounted or excluded, only drawn later.
3. **Different track title** over repeated/similar titles (avoids weak signal from near-duplicate tracks, e.g. live/remix variants).
4. **Longer track titles** over shorter ones (added 2026-07-12) — a short, generic title (e.g. "Love") is more likely to produce weak or ambiguous corroboration on the MusicBrainz side than a longer, more distinctive one.
5. **Shorter albums (< 20 tracks)** preferred over longer ones — large-track-count releases are disproportionately compilations/box sets/deluxe editions, more likely to have incomplete or split MusicBrainz data.

These rules affect **only feed order**, never the LLR sum directly — none of album length, credit count, or title length says anything about candidate correctness on its own, only about the likely cleanliness or completeness of the observation. Keep this out of the scoring math entirely.

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

**Retired (confirmed 2026-07-12): "Name similarity" (near-exact/close/poor) and "Alias match" (alone, and as a joint feature with recording-match) no longer appear as evidence types below.** This table pre-dated the current, more precise model of how name/alias comparison actually functions — see §5.3 Stage 1/Stage 2 and §5.4 for what replaced it: a one-time, zero-LLR admission gate (Stage 1) and a per-recording-lookup corroboration weight, `NameMatchWeight`/`AliasMatchWeight` (§10.3), multiplying whatever Corroboration Tier value (§6.3) that lookup contributes (Stage 2). Nothing in this section computes or caches a standalone name-similarity LLR value anymore.

### 6.1 Evidence Types and LLR Values (nats)

| Evidence | LLR | Notes |
|---|---|---|
| ProviderIds asserted MBID, confirmed | +5.0 | Tier 0, §5.1 — stronger than Tier 1 because asserted by prior tagging, but still verified, not blindly trusted |
| ProviderIds asserted MBID, confirmation failed | −4.0 | Strong negative — a stale/wrong tag is informative |
| Disambiguation/context match | +0.8 | |
| Work relationship: writer/composer/lyricist | +2.5 | Sourced from `work-rels`+`work-level-rels`, §5.4/§7.2 |
| Recording relationship: producer | +0.8 | Sourced from `artist-rels` on the recording, §5.4/§7.2 |
| Recording relationship: arranger | +0.5 | Recording-level by MB convention, not work-level |
| Album match — distinctive title | +1.5 | |
| Album match — generic title | +0.3 | "Greatest Hits", self-titled, etc. |
| Corroboration Tier 1 — full triple (Artist+Track+Album) | +3.5 | Near-decisive alone; supersedes standalone album-match for the same album |
| Corroboration Tier 2 — Artist+Track, no album | +1.8 | Strong, not alone sufficient for auto-accept |
| Corroboration Tier 3 — single field only | +0.5 | Background support |

**Known residual risk, deliberately deferred, not yet built (confirmed via real MusicBrainz data during a same-name-collision investigation, 2026-07-12):** the album match distinctive/generic split above already guards against generic *album* titles ("Greatest Hits", self-titled) diluting the album-match precursor — but Corroboration Tier 1/2/3 above currently applies the same flat LLR regardless of how distinctive the *track* or *album* title actually is. A genuinely ambiguous case was reasoned through (not yet found in real data): a same-named-artist collision where the observed track has a **generic title** (e.g. a song simply called "Love") on a **generic album** (e.g. "Greatest Hits" or a close variant) could plausibly survive the fallback ladder (`RecordingLookup.cs`, §5.4) down to a loose rung and pick up genuine (not spurious) weak corroboration against the *wrong* same-named candidate, since neither title field is distinctive enough on its own to rule that candidate out. A real, genuinely ambiguous case with these exact properties hasn't been located and tested yet — this is a reasoned-through risk, not a confirmed failure. Proposed future tweak, not built: extend the distinctive/generic treatment above into `CorroborationTierEvidenceCollector`'s own weighting (downweight a Tier 1/2/3 hit whose track/album title is itself generic), and consider track duration as an additional large-distance elimination signal once the data model supports it (`EmbyTrackCredit`, §4, currently carries no duration field at all).
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
| Artist (performer) | 1.0× |
| Composer, undecomposed/other relationship | 1.0× |

**Correction (confirmed 2026-07-12): default is neutral 1.0× across all three tiers.** An earlier version of this table listed non-neutral multipliers (Artist 0.85×, Composer 0.5×); that was stale and contradicted the actual confirmed decision — difficulty in this system is in *generating* a composer-only candidate at all (§5.3 Strategy C), not in discounting evidence once a candidate is found. `ScoringConfig.RoleWeights` remains a live, editable config surface (§10.3) so this can still be tuned later — it is neutral by default, not hardcoded to be neutral.

### 6.3 Corroboration Tiers

Categorical, not continuous — encode as a specific evidence type with the tier as its value:
- **Tier 1**: Emby's Artist + Track + Album all agree with MusicBrainz. Near-decisive alone.
- **Tier 2**: Artist + Track agree, no album corroboration. Strong, needs at least one supporting signal for auto-accept.
- **Tier 3**: single field match only. Weak, background support.

**Match-quality multiplier (confirmed 2026-07-12, applies to all three tiers above):** before role-weighting (§6.2) is applied, multiply the tier's LLR value by `ScoringConfig.NameMatchWeight` (default 1.0) if the recording result's artist-credit matched the candidate's primary name, or `ScoringConfig.AliasMatchWeight` (default 0.9) if it matched only via one of the candidate's registered aliases (`NameDistanceEvidenceCollector`'s `MatchedViaAlias` flag, §5.4). This is the entirety of how alias-vs-name match quality affects scoring — see §5.3 Stage 2.

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
| **C1** | `GET /ws/2/artist?query=artist:"{name}"&fmt=json` | **Strategy B (confirmed 2026-07-12 — see §5.3)**, and at the start of every resolution task generally | Candidate list: MBID, name, sort-name, disambiguation, type, area, life-span, **registered aliases (confirmed present inline in the real response, no extra `inc=` needed — verified 2026-07-12)**, MB's own text-relevance `score` | **Now load-bearing for candidate generation itself** (§5.3) — name/alias match against this response is the admission bar for `ScoringConfig.CandidateGeneration.ArtistCandidateMinScore`, not just a downstream evidence check |
| **C2** | `GET /ws/2/artist/{mbid}?inc=aliases+tags+release-groups+url-rels&fmt=json` | Once per candidate surviving C1's filter | Aliases, tags, release-groups, url-rels (external IDs) | Alias-match, tag-overlap, album-match (§5.2), external-ID evidence | Release-group/release browse endpoints **require** an explicit `type=`/`status=` filter or return empty — do not treat an unfiltered empty result as "no albums found." |
| **C3** | `GET /ws/2/recording?query=recording:"{track}"+AND+arid:{anchor_mbid}&fmt=json` | Strategy A or C | Matching recording(s), usually one: artist-credit, length, release-list | Duration filter; Tier 2/3 corroboration data included at no extra cost |
| **C4** | `GET /ws/2/recording?query=recording:"{track}"+AND+release:"{album}"&fmt=json` (fallback: drop `release:`) | **`RecordingLookup`'s fallback ladder (§5.4), as the per-candidate confirmation step for Strategy B** — previously this call's shape was Strategy B's own primary candidate-generation query (see Appendix A); as of the 2026-07-12 direction it's used to *confirm* an already name/alias-admitted candidate, not to generate the candidate pool itself | Same shape as C3, typically more candidates | Same as C3 — same score caveat applies |
| **C5** | `GET /ws/2/recording/{id}?inc=artist-credits+artist-rels+work-rels+work-level-rels&fmt=json` | On the surviving recording candidate from C3/C4 | Work-level relations (composer/lyricist/writer) **and** recording-level relations (producer/engineer/arranger) — see §5.4 for why both `inc` terms are required in one call | Composer-relationship LLR breakdown, §6.1 |
| **C6** | `GET /ws/2/artist/{candidate_mbid}?inc=artist-rels&fmt=json` | After C5 surfaces a writer/composer MBID | Artist-to-artist relationship list | Supporting corroboration check |

### 7.3 Unverified — Check Before Building

- Exact `inc=` parameter for ISNI/IPI identifier codes — not confirmed.
- Whether any relationship-type names referenced above have since been split into more specific subtypes (MusicBrainz has done this before, e.g. with "engineer").

---

## 8. Emby Integration

### 8.1 Cost Model

This is a server-side plugin: Emby API access means the injected C# service interfaces (`ILibraryManager`, etc.), **not HTTP calls** — there is no network round-trip and no rate limit. The real cost driver is field-hydration breadth (which fields a query pulls back), not per-artist query count: a single artist's own track read costs on the order of tens of milliseconds, and the pipeline is already strictly sequential (§12.1) and bottlenecked by MusicBrainz's 1 req/sec ceiling (§7.1) regardless — N per-artist reads spread across a run add negligible wall-clock cost against that ceiling.

**Two separate reads, not one broad pass, deliberately:**

1. A **lightweight, library-wide Tier Classification Pass** (name/id/role only, no track-level hydration) — run once per sync, used only to fix the wavefront's processing order (§3.2). Provisional and non-authoritative: an artist classified into the wrong wave here costs nothing downstream, since their tracks are read properly and bucketed correctly at their own processing time regardless (§8.2, call E2).
2. A **full Per-Artist Track Read**, done at processing time, one artist at a time — never upfront for the whole library. Holding hydrated track data (`People`/`ProviderIds`/`Album`/duration) for every `Audio` item in the library at once, whether in memory or in this engine's own SQLite tables, means carrying the equivalent of the full track database for a library that may run to hundreds of megabytes — for a marginal wavefront-ordering benefit that a lightweight classification-only pass already captures. Each artist's own read happens fresh, at negligible cost, exactly when it's needed instead.

### 8.2 API Call Catalog

| # | Call | Triggered By | Yields | Feeds |
|---|---|---|---|---|
| **E1** | `ILibraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { typeof(MusicArtist).Name }, Recursive = true, IsVirtualItem = false, EnableTotalRecordCount = false, DtoOptions = new DtoOptions(true) })` | Once per full library sync | Full artist list: Id, Name, sort-name — **confirmed complete, includes composer-only artists** (§8.2 below) | Enumeration set the wavefront iterates over; `EmbyArtistProvider.GetAll()` needs no union with any other source |
| **E1b** | `_libraryManager.GetAlbumArtists(new InternalItemsQuery(user) { Recursive = true, DtoOptions = <minimal fields only> })` and `_libraryManager.GetArtists(new InternalItemsQuery(user) { Recursive = true, ... })` — no `ParentId`, so both run library-wide across all folders, not scoped to one library | Once per full library sync | Two id sets returned as `QueryResult<(BaseItem, ItemCounts)>`: which `MusicArtist` ids are album artists, which are (any) artists — no track-level data | Provisional `artist_role_classification` (§9): id present in the `GetAlbumArtists` result ⇒ AlbumArtist tier; else present in `GetArtists` ⇒ Artist tier |
| **E1c** | `_libraryManager.GetItemList(new InternalItemsQuery(user) { IncludeItemTypes = new[] { typeof(Person).Name }, PersonTypes = new[] { "Composer" }, Recursive = true })` | Once per full library sync | List of `Person` items tagged `Composer` — modeled as a distinct entity type from `MusicArtist`, but returned ids are drawn from the **same id space** as `MusicArtist` (§8.3) | Provisional Composer-tier signal, joined to a `MusicArtist` id directly by id, not by name (§8.3) — the authoritative per-track Composer role still always comes from the real per-artist read (E2), never from this pass |

**Tier assignment from E1b/E1c** (priority AlbumArtist > Artist > Composer, exclusive — matches §6.2):

```
albumArtistIds  = GetAlbumArtists() ids
allArtistIds    = GetArtists() ids            -- anyone with any Artist/AlbumArtist credit
composerIds     = E1c Person/Composer ids     -- includes BOTH ids that overlap allArtistIds (an artist who
                                                 also composes) AND ids that don't overlap at all (a
                                                 composer-only artist, confirmed never to appear in
                                                 GetArtists(), below)

artistOnlyIds   = allArtistIds - albumArtistIds
composerOnlyIds = composerIds  - allArtistIds   -- nets out both AlbumArtist and Artist-only ids in one
                                                    step, since albumArtistIds ⊆ allArtistIds already
```

Each artist id lands in exactly **one** provisional bucket by construction of this subtraction order — no artist is double-counted, and the ordering itself encodes §6.2's AlbumArtist > Artist > Composer priority.

**Confirmed: E1 alone (`GetItemList` filtered to `IncludeItemTypes=MusicArtist`) is sufficient for enumeration — no composer-only artist is invisible to it.** Directly confirmed: an artist credited as composer on exactly one track, with no Artist/AlbumArtist credit anywhere else in the library, is returned by

```csharp
_libraryManager.GetItemList(new InternalItemsQuery
{
    Recursive = true,
    IncludeItemTypes = new[] { "MusicArtist" },
    IsVirtualItem = false,          // skips dummy/stub metadata objects
    EnableTotalRecordCount = false, // no pagination UI to serve here
    DtoOptions = new DtoOptions(true)
}).OfType<MusicArtist>().ToList();
```

This resolves the original enumeration concern outright: E1's row in §8.2 should use this exact shape (updated below), and `EmbyArtistProvider.GetAll()` needs no defensive union with E1c after all — E1 alone already returns everyone, composer-only artists included.

**This also resolves the earlier apparent contradiction, cleanly.** The UI's Artists tab not showing this artist isn't inconsistent with the above — they're different calls. `GetItemList` is a raw, generic item query; the UI is presumed to be backed by the dedicated `ArtistsService`/`GetArtists()`/`GetAlbumArtists()` methods (§8.2, E1b), which may apply their own additional filtering beyond raw entity existence (e.g. a "has at least one Artist/AlbumArtist credit" business rule for what counts as a browsable artist). That's a hypothesis, not yet confirmed — but it no longer needs to be framed as a contradiction to resolve.

**What's now open is narrower and more useful than before**: if the dedicated `GetArtists()` method does exclude composer-only artists (unconfirmed), its *absence* from that result becomes a clean, direct composer-only signal on its own — meaning tier assignment might only need E1 (full population) plus `GetArtists()`/`GetAlbumArtists()` (two dedicated calls), with no need for E1c's separate Person/`PersonTypes=Composer` query at all:

```
albumArtistIds  = GetAlbumArtists() ids
artistIds       = GetArtists() ids            -- hypothesis: excludes composer-only artists, unconfirmed
allIds          = E1's full MusicArtist list  -- confirmed complete, includes composer-only artists

artistOnlyIds   = artistIds - albumArtistIds
composerOnlyIds = allIds - artistIds          -- only valid if the hypothesis above holds
```

If `GetArtists()` turns out to include composer-only artists after all (i.e. behaves the same as raw `GetItemList`), this collapses `composerOnlyIds` to empty and E1c's Person/Composer query (§8.2) is still needed as the actual tier-tagging signal. Either way, **enumeration itself is settled** — this remaining question only affects how cleanly tier tagging can be done, and a wrong or missing tier tag here is low-stakes regardless (§8.1), since the authoritative per-track role always comes from each artist's own E2 read.

**REST remains available as an option for E1b/E1c specifically** (`GET /Artists`, `GET /Artists/AlbumArtists`, per the actual captured payload §8.2 is based on), if the dedicated C# methods prove awkward to call directly or their filtering can't be confirmed cleanly — not because of any remaining C#-vs-UI contradiction (that's resolved above). **Confirmed pattern if chosen**: an admin-supplied API key (`EngineConfig.EmbyRestFallback.ApiKey`, §10.1), sent as `X-Emby-Token` (matching the captured UI payload), through the injected `IHttpClient` abstraction — not a raw `.NET HttpClient` — matching a confirmed real precedent from a reference plugin's own refresh-trigger call. One adaptation from that precedent: the reference call is fire-and-forget (`Task.Run`, result discarded); E1b/E1c need the response body back before the Tier Classification Pass can proceed, so the call here is a direct `await`, not wrapped in `Task.Run`.
| **E2** | `ILibraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { typeof(Audio).Name }, Recursive = true, ArtistIds/AlbumArtistIds/ComposerArtistIds = new[] { internalId } })` — exact filter shape TBD, see §8.3 | **Once per artist, at that artist's own processing time** — not upfront, not for the whole library | This artist's tracks only: Name, Album, AlbumArtists, Artists, People, RunTimeTicks, ProviderIds, ParentId | Authoritative role reclassification, per-track bucketing (§5.5.1), incidental co-occurrence discovery (upserted into `artist_cooccurrence`, §9), duration checks, ProviderIds fast path — all from one per-artist call |
| **E3** | `ILibraryManager.GetItemById(Guid id)` | Targeted re-check only (content-fingerprint drift, manual re-resolution) | Single item DTO | Point verification — must not be used in a loop |
| **E4** | (field within E2's result, no separate call) | Every track, incidentally | `ProviderIds` dictionary | Tier 0 evidence, §5.1 |

**Requirement**: E2 is deliberately a per-artist query, issued fresh each time that artist is processed — see §8.1 for why this is preferred over a single whole-library pass. The whole-library calls retained are E1b/E1c, and both are deliberately minimal (role/tier only, no track hydration). Within a single artist's resolution, downstream stages read from that artist's own fresh E2 result directly; they do not re-query `ILibraryManager` for data it already returned, and no whole-library track cache is kept anywhere in the engine.

**Confirmation note on E1b/E1c**: `GetArtists`/`GetAlbumArtists` as dedicated `ILibraryManager` methods (rather than a single filtered query) are corroborated by a search-result snippet of Emby's own source (`MediaBrowser.Api/Library/LibraryService.cs`, in the real `MediaBrowser/Emby` repository — the same lineage as the already-cloned `Emby.AutoOrganize` reference plugin) calling `_libraryManager.GetArtists(query).Items...`. This is a snippet-level corroboration, not a full clone-and-read of `LibraryManager.cs` the way `Emby.AutoOrganize` was (§20) — a direct fetch of that file was blocked by the source host's robots policy. Confirm against a full local read of the actual `ILibraryManager`/`LibraryManager` source for the SDK version this plugin targets before relying on the exact signatures above.

**Incremental sync**: run as a scheduled task (§12), following the same idiom as the reference plugin this pattern was confirmed against, rather than reacting to live library-change events.

### 8.3 Unverified — Check Before Building

- **Whether the dedicated `GetArtists()` method (distinct from the raw `GetItemList` call E1 uses) excludes composer-only artists.** Not the same question as enumeration completeness, which is settled (§8.2) — this is narrower: whether `GetArtists()`'s absence can be used directly as a composer-only signal for tier tagging, or whether it behaves the same as `GetItemList` (in which case E1c's Person/Composer query is still needed for tagging). Low-stakes either way (§8.1), since a wrong provisional tier costs nothing downstream.
- **Whether to use the C# `ILibraryManager` calls or Emby's own REST API for E1b/E1c.** REST has a directly-observed, concrete payload behind it (the captured `/Artists/AlbumArtists` request §8.2 is built from); the C# signatures are only snippet-corroborated. The REST auth mechanism itself is confirmed (admin-supplied API key + `IHttpClient`, §8.2).
- Whether E1c's returned `Person` items always carry a usable internal id in the same numeric id-space as `MusicArtist` for the composer-only case — evidenced for the composer-*and*-artist case via id-sharing, not independently re-verified for a composer-only entity's id specifically.

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
artist_role_classification (artist_id, highest_role, source[provisional|authoritative], classified_at)
                        -- 'provisional' rows come from E1b/E1c (§8.2), used only to order the wavefront;
                        -- 'authoritative' rows come from each artist's own E2 read (§8.2) and supersede
                        -- the provisional row for that artist once processed
artist_cooccurrence     (artist_id, cooccurring_artist_id, recording_id, role_of_cooccurring)
                        -- populated incrementally, as each artist's own E2 read surfaces co-occurring
                        -- credits — there is no global co-occurrence pre-pass (§3.2, §8.1)
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
├── EmbyRestFallback   -- only relevant if the dedicated GetArtists()/GetAlbumArtists() C# calls prove awkward for E1b/E1c (§8.2, §8.3)
│   ├── Enabled: bool (default false — prefer the ILibraryManager calls unless/until needed)
│   ├── ApiKey: string (required if Enabled; admin-supplied, sent as X-Emby-Token, §8.2)
│   └── BaseUrl: string (default derived from the server's own configured address)
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

### 10.3 ScoringConfig (code-based for now; UI-editable grid deferred)

**Decision (confirmed during Phase 2 planning, superseding this section's original framing):** `ScoringConfig` stays a plain C# class with hardcoded default values, edited by a developer and shipped via rebuild — not a database-backed, grid-editable settings page. The original motivation for a live-editable grid still holds in principle (thresholds and weights need tuning to get good results, and that tuning shouldn't require touching the core pipeline logic) — but the knobs are for the development/tuning phase, and most are expected to get removed or hardcoded down once calibration settles them, not to remain a permanent user-facing surface.

**Door intentionally left open**: once this is packaged as a real Emby plugin with its own UI environment (§13), surfacing some subset of these values in a proper settings page becomes a natural, low-effort addition on top of the plugin's own config UI conventions — at that point it's a UI feature riding on infrastructure that already exists for other reasons, not a bespoke grid/versioning system built just for this. Revisit then; don't build the `scoring_config_overrides` table/versioning machinery speculatively now.

Row-shaped tuning data, one entry per §6 value, defaulting to the values listed there if absent:

```
ScoringConfig
├── CandidateGeneration.StrategyA/B.MaxCandidates, DurationToleranceSeconds
├── CandidateGeneration.ArtistCandidateMinScore: int (default 0, inert — MB's own C1 relevance score, §5.3/§5.4, reserved)
├── CandidateGeneration.ArtistCandidateMaxEditDistance: (Stage 1 admission gate, §5.3 — needs real-data tuning, no default yet asserted as correct)
├── CandidateGeneration.NameNormalizationRules[]   -- editable replacement/allowance table, §5.3 Stage 1 (strip "The", fold &/and/+, strip apostrophes, strip feat/vs/etc., seed list per Project Log)
├── NameMatchWeight: double (default 1.0), AliasMatchWeight: double (default 0.9)   -- Stage 2 corroboration multiplier, §5.3/§6.3
├── BucketCeiling { AlbumArtist: 3, Artist: 4, Composer: 6 }
├── ComputeAlbumMatchPrecursorOncePerCandidate: bool   -- static evidence rule now applies only to §5.2's precursor, not to name-similarity (retired, §6.1)
├── PreferDifferentAlbum, PreferSingleCreditTracks, PreferDifferentTrackTitle, PreferLongerTrackTitles: bool
├── PreferAlbumTrackCountBelow: int (default 20)
├── AutoAcceptThreshold, AutoRejectThreshold, MinMarginOverRunnerUp
├── EvidenceWeights[]   -- one row per §6.1 evidence type
├── RoleWeights { AlbumArtist: 1.0, Artist: 1.0, Composer: 1.0 }   -- neutral default, §6.2
├── ComposerRelationshipWeights { writer, producer, arranger, other }
├── AlbumMatchWeights { distinctiveTitle, genericTitle }
└── JointEvidencePairs[]   -- correlated-pair overrides, §5.4 (album-match/full-triple supersession only; alias+recording joint pair retired, §6.1)
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
│       ├── EmbyArtistProvider.cs         -- ISourceEntityProvider<EmbyArtist>, applies DeveloperConfig.ArtistFilter, unions E1's MusicArtist list with composer-only ids from E1c (§8.2)
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
│           ├── RecordingLookup.cs                -- shared per-(candidate,track) recording
│           │                                        lookup + fallback ladder (§5.4, confirmed
│           │                                        during Phase 2 planning, not part of the
│           │                                        original design) -- used by every
│           │                                        collector below that needs one, instead of
│           │                                        each calling SearchRecording independently
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

**Note**: the signatures below have been aligned to what Phase 1's actual implementation already uses (`IBeliefScorer.Score` takes `ScoringConfig`; `IDecisionGate.Decide` takes `sourceSystem`/`sourceId` to build the `MatchResult`) — both are small, evident gap-fills discovered during implementation, documented in the Project Log's Evidence section rather than left as an undocumented drift from this spec.

**Addition (Phase 2, confirmed during Sequential Sampler planning):** `IObservationUnit`, `IObservationEvidenceCollector`, and `IObservationUnitProvider` support §5.5's per-observation sampling generically. `IObservationUnit` is deliberately opaque to `Core` — for the Artist/MusicBrainz case a unit is one track and `BucketKey` is "AlbumArtist"/"Artist"/"Composer"; a future entity type with no natural role/bucket concept (§11.4's own Album example) can either use a single constant `BucketKey` or simply not implement `IObservationUnitProvider` at all (`IResolverPlugin.ObservationUnitProvider` is nullable for exactly this reason, and `ObservationEvidenceCollectors` can be an empty list). When both are absent, the Sequential Sampler scores from static evidence alone — identical to the pre-Phase-2 behavior.

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

    // Static, candidate-pair-level evidence (§5.4) -- computed once per candidate,
    // e.g. name similarity. Called once, not per observation.
    public interface IEvidenceCollector<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        string EvidenceType { get; }
        EvidenceRecord? Collect(TSourceEntity source, Candidate candidate, ResolutionContext context);
    }

    // Per-observation evidence (§5.4) -- re-run once per sampled IObservationUnit,
    // as many times as the Sequential Sampler (§5.5) draws units.
    public interface IObservationEvidenceCollector<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        string EvidenceType { get; }
        EvidenceRecord? Collect(TSourceEntity source, Candidate candidate, IObservationUnit unit, ResolutionContext context);
    }

    public interface IBeliefScorer
    {
        ScoredCandidate Score(Candidate candidate, IEnumerable<EvidenceRecord> evidenceSoFar, ScoringConfig config);
    }

    public interface IDecisionGate
    {
        MatchResult Decide(IEnumerable<ScoredCandidate> rankedCandidates, ScoringConfig config, string sourceSystem, string sourceId);
    }

    // The unit of extensibility for new target systems/entity types: one implementation
    // per (target system, target entity type) pair.
    public interface IResolverPlugin<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        string TargetSystem { get; }
        string TargetEntityType { get; }
        IEnumerable<ICandidateGenerationStrategy<TSourceEntity>> Strategies { get; }
        IEnumerable<IEvidenceCollector<TSourceEntity>> EvidenceCollectors { get; }
        IEnumerable<IObservationEvidenceCollector<TSourceEntity>> ObservationEvidenceCollectors { get; }  // empty if none
        IObservationUnitProvider<TSourceEntity>? ObservationUnitProvider { get; }                          // null if no observation concept
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

`IObservationUnit` itself lives in `Core/Model`, alongside the other model types (§4):

```csharp
namespace MetadataHealthCheck.v2.Core.Model
{
    public interface IObservationUnit
    {
        string BucketKey { get; }
    }

    // Optional -- entity types with no natural observation/role concept (§11.4's
    // Album example) simply don't provide one.
    public interface IObservationUnitProvider<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        // Outer sequence: buckets, in priority sampling order (e.g. AlbumArtist, Artist,
        // Composer, highest signal first). Inner sequence: units within that bucket,
        // already in distance-seeking sample order (§5.5.1).
        IEnumerable<IEnumerable<IObservationUnit>> GetOrderedBuckets(TSourceEntity source, ResolutionContext context);
    }
}
```

### 11.3 Requirements: Source Population & Filter Application Point

`EmbyArtistProvider.GetAll()` (§11.2) enumerates directly from E1's `MusicArtist` list — confirmed complete, including composer-only artists (§8.2), so no union with any other source is needed here. E1b/E1c (§8.2) are consulted only for provisional tier tagging of ids E1 already returned, not for discovering additional ones.

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
- Sampling — bucket ceilings, the four distance-seeking toggles (§5.5.1), `PreferAlbumTrackCountBelow`.
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

**Corrected 2026-07-12** against real MusicBrainz data (previously this section used entirely invented MBIDs/aliases and an evidence type — a −2.0 "no match" penalty for the runner-up — that doesn't correspond to anything in §6.1's actual catalog; see Project Log Evidence Log, 2026-07-12, for the full finding). This version uses the real, externally-verified data for Emby artist "Sarah Vaughan" and updates the flow to the confirmed artist-search-first candidate generation (§5.3).

**Setup**: Emby Artist "Sarah Vaughan" (AlbumArtist tier), one observed track: "Autumn Leaves" on album "Crazy and Mixed Up".

```
Step 1 — Strategy B, artist search (§5.3, real data):
  GET /ws/2/artist?query=artist:"Sarah Vaughan"&fmt=json
  Top result: id=351d8bdf-33a1-45e2-8c04-c85fad20da55, name="Sarah Vaughan",
              score=100 — exact name match, admitted as candidate X.
  Next result: id=23bdf915-67af-4089-b938-60efdaeab13f,
               name="Sarah Vaughan and Her Trio", score=63 — a real, distinct
               MusicBrainz entity, but its full name is not a close match to
               the source artist's own name ("Sarah Vaughan") and it carries
               no alias claiming otherwise. Does NOT clear the
               ArtistCandidateMinScore/name-closeness admission bar (§5.3) —
               excluded before candidate generation, not scored down
               afterward. No candidate Y exists in this trace.

Candidate X (only candidate):
  Album-match precursor: "Crazy and Mixed Up" is a real release-group for
    this artist (expected from real data; not independently re-confirmed by
    a separate release-groups call in this pass) — distinctive title
                                                                   llr += 1.5   → running = 1.5
  Observation 1 (AlbumArtist bucket, "Autumn Leaves"/"Crazy and Mixed Up"):
    Rung 1 of the recording-lookup ladder (§5.4) confirms immediately —
    real recording id 5dbea991-e5e9-4489-81a2-d5e8e13f161a, track title and
    release title both match exactly:
      Tier 1 full triple (Artist+Track+Album all agree)                llr += 3.5   (supersedes the 1.5 precursor for this album, §5.2)
      Name similarity, static, computed once (exact match)              llr += 1.5
  running_llr = 5.0

Margin: trivially satisfied (§5.7) — no runner-up exists, since "...and Her
  Trio" never entered the candidate pool. This is a genuine single-candidate
  case, not a margin comparison against a weak rival.
```

**Expected result**: auto-accept X after **one observation** — `BucketCeiling.AlbumArtist` (3) is never needed here; it's the ceiling this run stayed well under. This is the sequential-sampler behavior from §5.5, not a fixed-batch result.

**On multi-candidate margin behavior**: this specific real case doesn't exercise the margin-over-runner-up check, since only one candidate survives the artist-search admission bar. That mechanism is validated separately — a real same-name collision (MusicBrainz's two distinct "Nirvana" acts, 90s US grunge vs. 1967 UK psych-pop) was investigated during the same review and confirmed to be handled correctly by the sampler's joint per-round evaluation, given the two acts' catalogs don't overlap (Project Log Evidence Log, 2026-07-12). A genuinely hard multi-candidate case — one where a real recording's title is generic enough that a same-named rival could plausibly pick up real (not spurious) weak corroboration — remains a known, deliberately deferred residual risk (§6.1), not yet demonstrated against real data.

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
3. Whether the dedicated `GetArtists()` method (distinct from `GetItemList`, which is confirmed enumeration-complete) excludes composer-only artists — affects only how cleanly provisional tier tagging can be done, not enumeration, which is settled (§8.2, §8.3).
4. The exact call to obtain a logger scoped by name (§15.1).
5. Whether `BasePluginSimpleUI<T>` and `IHasWebPages` can be implemented on the same plugin class simultaneously (§13.2) — confirmed-safe fallback provided if not.

---

## 21. Phased Build Plan

Building the entire spec simultaneously is not a realistic first milestone. Build in this order; each phase is independently testable.

1. **Skeleton + one path end-to-end**: `Core` model/interfaces (§4, §11.2), SQLite storage (§9, core tables only), `Sources/Emby` (§8), `Resolvers/MusicBrainz` with **only Strategy A/B** and **only two evidence types** (name similarity, work-relationship). A simple weighted-sum scorer is acceptable here — Bayesian comes in phase 2. Goal: one artist resolved end-to-end, logged, stored.
2. **Full evidence set + Bayesian scoring**: remaining evidence collectors (§6), switch to `BayesianBeliefScorer`, add the sequential sampler (§5.5) and decision-gate margins. Goal: reproduce §18's worked example exactly.
3. **Tiering + anchoring**: provisional tier-classification pass (E1b/E1c, §8.2) for wavefront ordering, authoritative per-artist reclassification and incremental co-occurrence discovery from each artist's own E2 read (§8.1–§8.2, §9), Strategy C (§5.3). Goal: composer-only artists resolving via borrowed anchors.
4. **Active learning + calibration**: review queue, identity cache, calibration backtest job (§5.9, §16).
5. **Developer tooling + config pages**: `EngineConfig` SimpleUI page, Developer HTML/JS page with filters/granular clears/scoring grid (§13–14). Deliberately last — useful throughout development as plain hardcoded test values in the meantime, but not blocking earlier phases.

---

## Appendix A: Superseded Candidate-Generation Approach (Strategy B)

**Status: superseded 2026-07-12, kept for reference only.** This is the recording-search-first design §5.3 described before that date, and what `SoftBucketStrategy.cs` still implements as of this writing (the code has not yet been updated to match the confirmed direction in §5.3 — see the Project Log's coding checklist for what that update involves). Do not treat this as a live alternative to build against unless a fundamental flaw is found in the artist-search-first direction and this needs to be revisited.

> **Strategy B (broad fallback, superseded)**: if neither A nor C is available, query `recording:"{track}" AND release:"{album}"` (falling back to dropping the `release:` clause if empty) (§7.2, call C4). Broader result set, tighter duration filter (§5.4). Every distinct artist MBID returned became a candidate, admitted unconditionally — no name/alias gate was applied before candidate generation, and `ScoringConfig.CandidateGeneration.ArtistCandidateMinScore` was never actually implemented anywhere despite being named in the spec.

**Why this was superseded:** a design review against real MusicBrainz data (2026-07-12, Project Log Evidence Log) found this design relies entirely on downstream evidence (chiefly negative name-similarity) to cancel out spurious candidates after the fact, rather than never admitting them in the first place. Concretely: a recording search returns every artist who has ever recorded something matching the queried track/album text, with no upfront check that any of them are plausibly the artist actually being resolved. The artist-search-first design (§5.3, current) closes this by checking name/alias plausibility *before* a candidate is ever generated.

**What this approach still might be right for, if revisited:** cases where MusicBrainz's real artist-credit text for the correct candidate doesn't resemble the Emby-tagged name and isn't in MusicBrainz's own registered alias list — a genuine data-completeness gap that could cause artist-search-first to miss a real candidate the recording-first approach would have found. No real example of this has been located and confirmed yet; if one is, it's the concrete trigger for revisiting this appendix.
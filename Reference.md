


---




### 5.1 ProviderIds Fast Path (Tier 0)

Before any candidate generation: check the source track's `ProviderIds` for
an existing MusicBrainz identifier (commonly written by taggers like Picard
at rip time, surfaced by Emby's item data — §8.3). If present, issue one
anchored recording lookup (§7.2, call C3) to confirm it. On confirmation,
record as Tier 0 evidence (LLR +5.0, §6) and skip candidate generation
entirely. On failure, record as strong negative evidence (LLR −4.0) and
continue to normal candidate generation — a failed assertion is informative,
not fatal.

### 5.2 Album-Match Precursor (noise to be removed)

Once per candidate (not per track): fetch the candidate's MusicBrainz
release-group list (§7.2, call C2) and string-match against Emby's known
album list for the source artist. Weight distinctive titles higher than
generic ones (e.g. "Greatest Hits", self-titled — §6). This is the first
observation fed into the Sequential Resolution Engine (§5.5) — if it alone
crosses the accept bound, no track-level sampling is needed.

**Supersession rule**: if a later observation produces a Tier 1 full-triple
corroboration (§6.3) for the same album, that observation supersedes this
precursor's contribution for that album — resolved at scoring time
(`SimpleWeightedSumScorer`), not collection time, since the precursor always
runs before any per-track observation exists.

### 5.3 Candidate Generation (noise to be removed)
- **Strategy A (own anchor)**: if the source artist already has a confirmed
  MBID anchor, query `recording:"{track}" AND arid:{anchor_mbid}` (§7.2, call
  C3). Near-certain single hit. Even a single hit is passed through full
  evidence scoring — an anchor does not bypass verification.

- **Strategy C (borrowed anchor — Composer tier only, not yet built)**: if no
  own anchor exists, but a co-occurring artist on the same recording (from
  `artist_cooccurrence`, §9) is already resolved, use *that* artist's
  confirmed MBID as the anchor for the same query shape as Strategy A.
  Record the anchor dependency (`anchor_dependencies`, §9) with the anchor's
  confidence at time of use, so an overturned anchor can trigger a cascade
  re-open.

  **Parked extension, not built**: doing this safely long-term requires each
  track *observation* (not just the candidate being evidenced) to carry the
  co-occurring performer's **Emby artist entity ID**, not just a name string
  (names collide). No current interface carries that. Revisit once Strategy
  C sees real usage and `IObservationUnit` (§11.2) can be extended.

- **Strategy B (artist-search-first), two distinct stages:**



### 9.2 Item ID Drift Handling (if it became an issue revisit)

Primary key for tracked entities is the Emby ItemId. Alongside it, store a
content fingerprint (normalized title + album + duration, hashed, for
tracks; name + earliest-seen-release, for artists) in `content_fingerprints`.
On each sync, if a previously-known ItemId is missing, search recently-added
unmapped items for a fingerprint match before treating it as new — if found,
carry over the resolution history to the new ItemId and log the carry-over
explicitly.

---

## 11. Class structure (at specific time not up to date)

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
│       │   ├── AnchoredRecordingStrategy.cs      -- Strategy A, C3 (see §5.3 — not in the active Strategies array)
│       │   ├── SoftBucketStrategy.cs             -- Strategy B, C4
│       │   ├── AnchorByAssociationStrategy.cs    -- Strategy C
│       │   └── ProviderIdFastPathStrategy.cs     -- §5.1
│       └── Evidence/
│           ├── RecordingLookup.cs                -- shared per-(candidate,track) recording lookup + fallback ladder (§5.4), used by every collector that needs one
│           ├── NameMatchEvaluator.cs             -- Levenshtein/normalization utility, used by Stage 1 admission gate and RecordingLookup's match confirmation (§5.4). Not an evidence collector — name/alias closeness is never scored, see §5.4.
│           ├── AliasEvidenceCollector.cs         -- NOT built, deliberately: role folded into Stage 1/2 (§5.3), see §11.2
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

    // Static, candidate-pair-level evidence (§5.4) -- computed once per candidate.
    // Called once, not per observation.
    public interface IEvidenceCollector<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        string EvidenceType { get; }
        EvidenceRecord? Collect(TSourceEntity source, Candidate candidate, ResolutionContext context);
    }

    // Per-observation evidence (§5.4) -- re-run once per sampled IObservationUnit,
    // as many times as the Sequential Resolution Engine (§5.5) draws units.
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

    public interface IObservationUnit
    {
        string BucketKey { get; }   // "AlbumArtist" | "Artist" | "Composer" for the Artist case; opaque to Core
    }

    public interface IObservationUnitProvider<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        // Ordered outer list = bucket priority order; ordered inner list = within-bucket feed order (§5.5.1).
        IEnumerable<IEnumerable<IObservationUnit>> GetOrderedBuckets(TSourceEntity source, ResolutionContext context);
    }
}
```



## 17. Testing strategy
Two distinct layers — do not conflate them.

### 17.1 Unit Tests (xUnit — default; swap if you have a strong reason)

Fast, deterministic, no network, no live Emby dependency. Cover:
- Scorer LLR combination math against known evidence bags (§18.1's worked example should be a literal test case).
- Sequential sampler stopping behavior against synthetic observation sequences.
- Evidence collectors' parsing of fixture MusicBrainz JSON responses (real recorded responses saved to disk, replayed by a test-mode `IMusicBrainzApiClient` implementation).
- `DeveloperConfig` CSV-filter parsing (§10.2).
- Repository CRUD and migration correctness against a temporary SQLite file per test.

**Note**: `SmokeTest/Program.cs`'s manual-assertion console harness is
currently paused as a coding-cycle activity — see `Decisions.md`. This
section describes the intended eventual unit-test layer, not current
practice.

### 17.2 Real-World Validation (not a unit test)

The §19 comparison run and the §16 calibration job. No fixture-based test
can establish that the engine is *right* about the real world, only that
it's internally consistent with its own rules.



## 18. Worked Example

One concrete trace, for validating an implementation matches intent —
should become an actual test case per §17.1. Uses real, externally-verified
data for Emby artist "Sarah Vaughan."

**Setup**: Emby Artist "Sarah Vaughan" (AlbumArtist tier), one observed
track: "Autumn Leaves" on album "Crazy and Mixed Up".

```
Step 1 — Strategy B, artist search (§5.3, real data):
  GET /ws/2/artist?query=artist:"Sarah Vaughan"&fmt=json
  Top result: id=351d8bdf-33a1-45e2-8c04-c85fad20da55, name="Sarah Vaughan",
              score=100 — exact name match, admitted as candidate X.
  Next result: id=23bdf915-67af-4089-b938-60efdaeab13f,
               name="Sarah Vaughan and Her Trio", score=63 — a real, distinct
               MusicBrainz entity, but its full name is not a close match to
               the source artist's own name and it carries no alias claiming
               otherwise. Does NOT clear the admission bar (§5.3) — excluded
               before candidate generation. No candidate Y exists in this trace.

Candidate X (only candidate):
  Album-match precursor: "Crazy and Mixed Up" is a real release-group for
    this artist — distinctive title
                                                                   llr += 1.5   → running = 1.5
  Observation 1 (AlbumArtist bucket, "Autumn Leaves"/"Crazy and Mixed Up"):
    Rung 1 of the recording-lookup ladder (§5.4) confirms immediately —
    real recording id 5dbea991-e5e9-4489-81a2-d5e8e13f161a, track title and
    release title both match exactly:
      Tier 1 full triple (Artist+Track+Album all agree)                llr += 3.5   (supersedes the 1.5 precursor for this album, §5.2)
  running_llr = 5.0

Margin: trivially satisfied (§5.7) — no runner-up exists, since "...and Her
  Trio" never entered the candidate pool. This is a genuine single-candidate
  case, not a margin comparison against a weak rival.
```

**Expected result**: auto-accept X after **one observation** —
`BucketCeiling.AlbumArtist` (3) is never needed here.

**On multi-candidate margin behavior**: this specific case doesn't exercise
the margin-over-runner-up check, since only one candidate survives the
admission bar. See `Evidence_Log.md` for the Nirvana same-name-collision
case, which validates that mechanism separately. A genuinely hard
multi-candidate case (generic track/album titles) remains a known,
deliberately deferred residual risk (§6.1).

---




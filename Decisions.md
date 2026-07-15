Decisions — MetadataHealthCheck.v2

Settled architectural calls. Each is final unless a new decision explicitly
supersedes it here (leave the old one, mark it superseded — don't delete).
Full reasoning/evidence behind any of these lives in Evidence_Log.md if you
need it; this file is the answer, not the argument.

---

### Candidate generation is artist-search-first, not recording-search-first
**2026-07-12.** Search by artist name+aliases first (Stage 1 admission gate,
zero LLR), then use recording-presence as confirmation (Stage 2). Inverts
the old "recording first, name-check after" flow. Accepted trade-off: risks
excluding a correct candidate if MB's own artist-credit text/alias list
doesn't resemble the Emby-tagged name (a genuine MB data-completeness gap,
not hypothetical) — accepted because ground-truth Emby tagging accuracy is
the user's responsibility, not the engine's to solve for. Old design kept in
`Superseded_Designs.md` in case this is found flawed later.
Spec: §5.3.

### SoftBucketStrategy is the primary, always-on path — not a fallback
**2026-07-13.** Runs every time; not "tried after Strategy A fails." The
class name is a holdover from the old design and no longer describes its
role — a rename is tracked (cosmetic, batch with other cleanup).

### AnchoredRecordingStrategy is not a competing candidate source
**2026-07-13, executed same day.** It's an unimplemented refinement OF the
main strategy, not a second candidate list to union — when a real anchor
exists, that should inform SoftBucketStrategy's own gate/rung behavior, not
run in parallel. Removed from the active `Strategies` array (was causing a
real bug: unconditional dual-run with no dedup by TargetId, splitting
evidence for any MBID both found). File untouched. Real design for
anchor-aware behavior is open — see Project Log Next Steps.

### RoleWeights stay neutral (1.0×) across AlbumArtist/Artist/Composer
**Confirmed, not to be relitigated.** The difficulty with composer-only
artists is entirely in generating them as a *candidate* at all (Strategy C),
not in the evidence being weaker once genuinely found. Kept as a live,
editable config surface for future tuning, not hardcoded neutral.
Spec: §6.2.

### Name/alias closeness is a gateway, never a scored evidence type
**2026-07-13.** Used in exactly two places: (1) Stage 1 admission gate
(§5.3, zero LLR), (2) RecordingLookup's per-recording match confirmation,
which drives the `NameMatchWeight`/`AliasMatchWeight` multiplier on
Corroboration Tier evidence (§6.3). `NameDistanceEvidenceCollector` (which
used to also emit a third, independent `NameSimilarity.*` additive LLR line
— double/triple-counting the same fact) removed from scoring entirely,
renamed to `NameMatchEvaluator.cs`, kept only for its shared Levenshtein/
normalization helpers.
Spec: §5.4, §6.1 (retirement).

### §5.2 album-match supersession resolved at scoring time, not collection time
**Confirmed during Phase 2 planning.** `AlbumMatchEvidenceCollector` always
runs once, before any per-track observation exists — it can't know in
advance whether a later Tier 1 hit will arrive for the same album. Handled
in `SimpleWeightedSumScorer` as a narrow, explicit filter, not the general
`JointEvidencePairs` mechanism (which stays unbuilt for any other case).

### Recording lookups use a 3-rung fallback ladder, accepted risk not solved
**Confirmed during Phase 2 planning.** track+artist+album → track+album →
track alone. Each rung risks its own false-positive shape; no clean design
exists to avoid this entirely. Relies on `NameMatchEvaluator`'s rejection of
poor matches as the real safety net. Shared/memoized once
(`RecordingLookup.cs`) rather than reimplemented per evidence collector.
Spec: §5.4, §7.2 (C4).

### ScoringConfig stays a plain C# class, not a database-backed settings UI
**Confirmed during Phase 2 planning.** Most knobs are for the tuning phase
and expected to get hardcoded down once calibration settles them, not to
remain a permanent user-facing surface. Door left open to surface a subset
in a real settings page once this ships as an actual Emby plugin (§13) —
revisit then, don't build the versioning machinery speculatively now.
Spec: §10.3.

### api_cache / ApiCacheRepository deliberately deferred
**2026-07-12.** Building the cache key/table shape before the real
MusicBrainz lookup shape (the fallback ladder) was settled would have meant
guessing. Revisit once the ladder's real query shapes are exercised against
more than fixture data.

### Terminology: "Track Observation Feeder" vs "Sequential Resolution Engine"
**2026-07-12.** Previously both called "Sequential Sampler," ambiguously.
Feeder (§5.5.1) selects/orders which track to observe next — no candidate
or LLR knowledge. Engine (§5.5) accumulates evidence and decides when to
stop. Keep these separate in naming and in any future code comments.

### Strategy C is concretely necessary, not just planned
**2026-07-11, real-world testing (Gus Black, Del Serino).** Both current
candidate-generation strategies are driven entirely by recording performer
credits; work-relationship credits never generate candidates. A pure
composer with no performer credits of their own is structurally unreachable
today. This is the load-bearing reason Strategy C exists — treat it as
confirmed-necessary when prioritizing next steps, not speculative scope.

### Concurrency: none, anywhere in the pipeline
Sync task, tiered wavefront, per-entity resolution, sequential sampler all
run strictly sequentially — one artist, one observation at a time. No
parallel evidence collection or candidate scoring.
Spec: §12.1.

### Deployment: v2 is fully decoupled from the existing prototype
Own SQLite DB, own config, own scheduled task. Both can run side-by-side
against the same library for comparison before cutover.
Spec: §19.1.

### SQLite patterns match Emby.AutoOrganize exactly, confirmed by source
Confirmed by cloning and reading `github.com/MediaBrowser/Emby.AutoOrganize`
directly, not re-derived from the spec's prose summary of it. Don't guess
Emby plugin framework details from spec prose — read the real reference
source if in doubt.

### Smoke testing is paused
**2026-07-13.** Not part of the coding cycle, not a next step, not a
blocker, until explicitly reintroduced. Holds until a strategy shape
(candidate generation architecture) is agreed and implemented.
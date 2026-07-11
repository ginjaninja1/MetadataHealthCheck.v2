Directives
Capture directives here
- https://github.com/ginjaninja1/MetadataHealthCheck.v2
- Build in phased order per spec §21; update this log at the end of each unit of work after testing has confirmed state (this file's own standing instruction).
- v2 runs against its own SQLite database/config, entirely decoupled from the existing prototype (§19.1) — confirmed respected throughout.
- Emby plugin framework is netstandard2.0. SQLite approach is settled as matching the patterns used in the existing Emby plugin Emby.AutoOrganize (https://github.com/MediaBrowser/Emby.AutoOrganize) — confirmed by cloning and reading that repo directly, not re-derived independently.
- Do not guess Emby plugin framework details from the spec's prose description of it; read the actual reference source. The spec is a summary, not a substitute for the real source.


Build Log
What is done, what is in progress, and what is planned for the project.


Status as of 2026-07-11


Sequential Sampler (§5.5) built and integrated — Phase 2's central
mechanism, replacing Phase 1's collect-everything-then-score-once loop
with adaptive, early-stopping, multi-candidate joint evaluation. Built
alongside a new entity-agnostic observation-unit abstraction so future
non-Artist resolvers aren't blocked on Artist-specific assumptions
(confirmed against §11.4's own Album example before committing to the
design). All 34 SmokeTest assertions pass, including 8 new real-world
and edge-case scenarios (see Phase 2 entry below and Evidence Log for
detail). Two structural gaps confirmed by real-world testing, not just
anticipated by the spec: composer-only artists cannot be resolved as
candidates at all today (motivating Phase 3's Strategy C), and no
recording/performer-tier evidence collector exists yet (Phase 2 scope,
not started). Two design decisions confirmed and closed off from further
relitigation: RoleWeights stays neutral across all tiers, and
ScoringConfig stays a plain developer-edited class rather than a
database-backed settings UI (door left open to add one later if this
ships as a real Emby plugin).


Status as of 2026-07-10


MetadataHealthCheck.v2.csproj builds and runs against real NuGet packages
(netstandard2.0, mediabrowser.server.core 4.9.1.90,
SQLitePCL.pretty.core 1.2.2) for the first time — previously only
validated against a local shim. SmokeTest runs against the real project
directly, using a new InMemoryMatchRepository in place of the real
SQLite-backed MatchRepository. All 8 Phase 1 assertions pass on this
configuration.
Real SQLite persistence via MatchRepository/SQLitePCL.pretty.core is
unverified. No compatible SQLitePCLRaw provider version was found for
standalone use (2.1.11 and 1.1.14 both fail). Not a blocker for real
deployment — Emby's own host process initializes the provider before any
plugin code runs — but open until checked inside an actual Emby host.
Storage/Sqlite/NativeSqlite.cs and SqliteConnectionWrapper.cs (dead,
unreferenced, and non-compiling under netstandard2.0) are deleted.
Storage/Sqlite/SqliteExtensions.cs added — TryBind/ExecuteQuery/
RunQueries, ported from Emby.AutoOrganize's own Data/SqliteExtensions.cs
(previously missing; not part of the real SQLitePCL.pretty.core package).
NuGet.Config files (repo root, SandboxValidation/, SmokeTest/,
SQLitePCLPrettyShim/) that cleared all package sources are deleted.
EmbyArtistProvider.cs's ArtistFilter split no longer uses
StringSplitOptions.TrimEntries or the single-char Split overload —
neither is guaranteed present in netstandard2.0.

Class tree changes (see full tree below for context):


Storage/Sqlite/SqliteExtensions.cs — Completed (new)
Storage/Sqlite/NativeSqlite.cs, SqliteConnectionWrapper.cs — removed (were dead Phase-1a code, untracked in the tree, never actually deleted until now)
SmokeTest/InMemoryMatchRepository.cs — added, sandbox/test-only

## Phase 1 — Skeleton + one path end-to-end — DONE, tested 2026-07-08

Scope per §21 phase 1: Core model/interfaces, SQLite storage (core tables only),
Sources/Emby, Resolvers/MusicBrainz with only Strategy A/B and only two evidence
types (name similarity, work-relationship), simple weighted-sum scorer.
Goal: one artist resolved end-to-end, logged, stored. **Goal met.**

**What was built:**
- Core/Model: `ISourceEntity`, `Candidate`, `EvidenceRecord`, `ScoredCandidate`,
  `MatchResult`, `ResolutionContext` (§4), plus a Phase-1-scoped `ScoringConfig`
  subset (full grid-editable version is Phase 5).
- Core/Interfaces: all of §11.2's interfaces (`ISourceEntityProvider`,
  `ICandidateGenerationStrategy`, `IEvidenceCollector`, `IBeliefScorer`,
  `IDecisionGate`, `IResolverPlugin`, `IIdentityCache`, `IMatchRepository`).
- Sources/Emby: `EmbyArtist`, `EmbyArtistProvider` (applies `ArtistFilter`
  parsing per §11.3), `IEmbyLibraryReader` abstraction over the real E2 query.
- Resolvers/MusicBrainz: `MusicBrainzArtistResolverPlugin` wiring Strategy A
  (`AnchoredRecordingStrategy`) and Strategy B (`SoftBucketStrategy`), two
  evidence collectors (`NameDistanceEvidenceCollector`,
  `WorkRelationshipEvidenceCollector`), `SimpleWeightedSumScorer`,
  `ThresholdDecisionGate` (§5.7's three-way outcome).
- Core/Engine: `ResolutionEngine` — identity-cache check → candidate
  generation (priority order) → evidence collection (all collectors run to
  completion; no early-stop yet) → scoring → decision gate → repository
  writes → identity-cache write on auto-accept. This is a valid but less
  API-efficient predecessor of §5.5's Sequential Sampler, which is Phase 2.
- Diagnostics: `StructuredLogger`, Console+buffer sink for now (real
  Emby `ILogger` scoped-by-name wiring is an open item, §20.4).
- Fixtures: `FixtureEmbyLibraryReader`, `FixtureMusicBrainzApiClient` — real
  recorded-response-style stand-ins per §17.1's fixture testing philosophy,
  loosely reproducing §18's worked example (exact reproduction is a Phase 2
  goal, since it needs corroboration-tier/album-match evidence not yet built).

**Testing performed (SmokeTest console app, `dotnet run`):**
1. Fixture Emby read returns exactly one artist. PASS
2. End-to-end resolution of "Sarah Vaughan": 2 candidates generated (X, Y),
   both evidence collectors fire, X scores running_llr=4.00 (confidence
   0.982) vs Y's 0.00, decision = auto_accept, correct candidate (X) wins.
   PASS
3. Second resolution of the same artist short-circuits via identity cache
   (no candidate generation re-run), logged as an "Identity cache hit" line.
   PASS
4. Match result round-trips through real SQLite (write via `MatchRepository`,
   read back via `GetExisting`) — confirms the storage layer is doing real
   persistence, not just holding state in memory. PASS
5. Edge case: an artist with no genuine MusicBrainz overlap does not crash
   and lands on `needs_review` (LLR too low for auto-accept, not all
   candidates below auto-reject either). PASS

All 8 assertions passed in this unit.

## Phase 1b — Align to Emby.AutoOrganize reference patterns — DONE, tested 2026-07-09

Directive received: target framework must be netstandard2.0 (real Emby plugin
constraint), and the SQLite approach is settled as "match the patterns in
Emby.AutoOrganize" — not a suggestion to re-derive independently. Cloned the
reference repo and read its actual source (not just inferred from the spec's
description of it) before changing anything.

**Confirmed directly from the reference plugin's own source:**
- `.csproj` targets `netstandard2.0`, with `PackageReference` to
  `mediabrowser.server.core` (4.8.0.13-beta) and `SQLitePCL.pretty.core`
  (1.2.2) — both from NuGet.
- `Data/BaseSqliteRepository.cs`: single shared `_connection` field, opened via
  `SQLite3.Open(path, ConnectionFlags.Create|ReadWrite|PrivateCache|NoMutex, ...)`,
  `CreateConnection()` returns the shared connection (or `_connection.Clone(false)`
  once one exists), WAL + `synchronous=Normal` set on init,
  `GetColumnNames`/`AddColumn` migration idiom, `ReaderWriterLockSlim` guard,
  real teardown only via an explicit `.Close()` inside `DisposeConnection()` —
  ordinary `using (var connection = CreateConnection())` blocks throughout the
  codebase are expected to survive past their own `Dispose()`.
- `Data/SqliteFileOrganizationRepository.cs`: CRUD via
  `connection.PrepareStatement(sql)` + named `@Param` binding via
  `statement.TryBind(...)`, `RunInTransaction(db => {...}, TransactionMode)`
  for writes, `foreach (var row in statement.ExecuteQuery())` for reads,
  `RunQueries(string[])` for batched DDL.
- `PluginEntryPoint.cs`: confirms §12.3's composition-root description exactly
  (constructor-injected native Emby services, manual construction of the
  plugin's own services in `Run()`, static `Current` singleton).

**What changed in the v2 source:**
- `MetadataHealthCheck.v2.csproj`: `net8.0` → `netstandard2.0`, added the two
  real `PackageReference`s above.
- Replaced the Phase-1a P/Invoke `NativeSqlite`/`SqliteConnectionWrapper` pair
  with `Storage/Sqlite/BaseSqliteRepository.cs`, ported line-for-line in
  structure from the reference plugin (one intentional deviation: takes this
  project's own `StructuredLogger` instead of `MediaBrowser.Model.Logging.ILogger`
  — wiring the real Emby-hosted logger is still gated on building inside an
  actual Emby host, §15.1).
- Rewrote `Storage/Sqlite/MatchRepository.cs` to extend `BaseSqliteRepository`
  and use the confirmed `PrepareStatement`/`TryBind`/`RunInTransaction` idiom
  instead of positional `?N` binding.

**The NuGet problem, addressed head-on rather than worked around silently:**
`nuget.org` is still unreachable from this build sandbox, so
`MetadataHealthCheck.v2.csproj` (as written, targeting netstandard2.0 with the
two real package references) cannot restore/build *here*. Rather than leave
the "real" source untested, added:
- `SQLitePCLPrettyShim/` — a small sandbox-only library implementing the exact
  subset of `SQLitePCL.pretty`'s public API surface
  (`IDatabaseConnection`, `IStatement`, `IResultSet`, `SQLite3.Open`,
  `TryBind`, `RunInTransaction`, etc.) that this project's storage layer uses,
  backed by direct P/Invoke against the system `libsqlite3.so.0`. Not shipped.
- `SandboxValidation/` — compiles the **exact same source files** from
  `MetadataHealthCheck.v2/` (via a `Compile Include` glob, no code
  duplication) against `net8.0` + the shim instead of the real packages.
  This is what actually gets built/run in this sandbox.
- `SmokeTest` now references `SandboxValidation`, not the real project
  directly.

This means: the source that ships is netstandard2.0 + real
`SQLitePCL.pretty.core`/`mediabrowser.server.core`, matching the reference
plugin exactly, and it is never edited to accommodate the sandbox — only a
side-channel test harness is. When this is eventually built somewhere with
normal NuGet access, build `MetadataHealthCheck.v2.csproj` directly; the
shim/SandboxValidation folders are sandbox scaffolding only and don't need to
travel with the plugin.

**Testing performed**: re-ran the full Phase 1a smoke-test suite (all 8
assertions from before) against the rewritten storage layer. All passed,
including real SQLite persistence and round-trip through the new
`BaseSqliteRepository`/`MatchRepository` pattern.

**Bug found and fixed during this rework** (see Evidence Log): the shim's
first draft closed the underlying SQLite handle on every `Dispose()`, which
broke the shared-connection reuse pattern the reference plugin relies on
(`using (var connection = CreateConnection())` in every method, on the same
underlying connection, repeatedly). Real teardown must only happen via an
explicit `.Close()` call, never via an incidental `using`-block `Dispose()`.
This is now understood and documented, and matters again if/when a
`MediaBrowser.Model.Logging.ILogger`-integrated version replaces
`StructuredLogger`.

**Deliberately deferred to later phases (per §21, not oversights):**
- Sequential Sampler (§5.5) and its early-stopping/one-at-a-time budget — Phase 2.
- Bayesian scorer, role-weight multipliers, joint-evidence rules — Phase 2.
- Remaining evidence types (album-match, corroboration tiers, aliases,
  disambiguation, entity-type sanity, tag/genre, external ID) — Phase 2.
- Strategy C (borrowed anchor), role classification, co-occurrence graph — Phase 3.
- Active learning, review queue, persistent identity_cache/negative_cache/
  api_cache tables, calibration backtest — Phase 4.
- EngineConfig/DeveloperConfig pages, scoring grid, developer clear-operations
  route — Phase 5.
- Real `IEmbyLibraryReader` implementation against `ILibraryManager`, and real
  `IMusicBrainzApiClient` implementation against musicbrainz.org — both need an
  environment with access to the real Emby SDK assemblies and outbound access
  to musicbrainz.org, neither available in this build sandbox. Interfaces are
  in place; only the live-network/live-host implementations are outstanding.
- Real `MediaBrowser.Model.Logging.ILogger` wiring in `BaseSqliteRepository`
  (currently takes this project's own `StructuredLogger`) — needs a real Emby
  host to confirm the exact "get a logger scoped by name" call (§15.1, §20.4).

## Phase 2 (in progress) — Sequential Sampler + interface extensibility — this increment DONE, tested 2026-07-11

Scope of this increment (not all of Phase 2 — see Class Tree below for what
remains): §5.5's Sequential Sampler, replacing Phase 1's "collect all
evidence, then score once" with adaptive, early-stopping, multi-candidate
joint evaluation per observation round. Built alongside a new, deliberately
entity-agnostic `IObservationUnit`/`IObservationEvidenceCollector`/
`IObservationUnitProvider` abstraction (§11.2, §11.4) so a future non-Artist
resolver isn't blocked on this — confirmed against §11.4's own Album example
before committing to the shape.

**What was built:**
- Core/Model: `IObservationUnit` — deliberately opaque to Core (§11.4).
- Core/Interfaces: `IObservationEvidenceCollector<T>`, `IObservationUnitProvider<T>`;
  `IResolverPlugin<T>` extended with both (nullable/empty-list when an entity
  type has no observation concept — graceful degrade to Phase 1 behavior).
- Core/Engine/`SequentialSampler.cs` — static evidence once per candidate,
  then bucket-by-bucket, unit-by-unit sampling with all live candidates
  scored and decision-gate-checked jointly after every single observation
  (not one candidate run to completion in isolation — required by the
  margin check between top candidate and runner-up).
- Core/Engine/`ResolutionEngine.cs` — `ResolveOne` now delegates to
  `SequentialSampler` instead of its old flat per-candidate loop.
- Core/Model/`ScoringConfig.cs` — added `BucketCeiling` (AlbumArtist 3,
  Artist 4, Composer 6 — safety caps, not targets) and `RoleWeights` (all
  neutral 1.0 — **confirmed decision**: composer-tier evidence is not
  weighted down; difficulty is in generating a composer-only candidate at
  all, not in the evidence being weaker once found — see Evidence Log).
- Sources/Emby: `EmbyTrackObservationUnit.cs` (new), `EmbyArtistObservationUnitProvider.cs`
  (new) — AlbumArtist→Artist→Composer bucket order, distance-seeking sample
  order within a bucket (2 of §5.5.1's 4 rules implemented against real
  data on `EmbyTrackCredit`; the other 2 need data this model doesn't carry
  yet — documented in-file, not faked).
- Resolvers/MusicBrainz/Evidence/`WorkRelationshipEvidenceCollector.cs` —
  converted from `IEvidenceCollector` (looped over all tracks itself,
  stopped at first hit) to `IObservationEvidenceCollector` (checks one
  sampler-supplied track at a time).
- Resolvers/MusicBrainz/`MusicBrainzArtistResolverPlugin.cs` — wires
  `ObservationEvidenceCollectors`/`ObservationUnitProvider`.
- Scoring/`SimpleWeightedSumScorer.cs` — applies `RoleWeights` multiplier
  (currently a no-op at 1.0 everywhere, but wired so it isn't dead config).
- Fixtures/`FixtureMusicBrainzApiClient.cs` — `SearchRecording` made
  track-title-sensitive (closes the looseness flagged 2026-07-10); added
  real-world data for 4 additional cases (see Evidence Log).
- SmokeTest/`Program.cs` — 8 new scenarios, 26 new assertions (34 total
  in the suite, up from Phase 1's 8).

**Testing performed (SmokeTest console app, `dotnet run`):**
1. Original Phase 1 "Sarah Vaughan" scenario re-verified under the new
   sampler architecture: identical result (auto_accept, running confidence
   0.98, resolved after exactly 1 observation in bucket AlbumArtist) —
   confirms the refactor didn't regress Phase 1. PASS
2. Multi-track adaptive stop: 2 AlbumArtist tracks, first carries no
   evidence, second does — sampler correctly draws both (not 1, not
   short-circuited on a false accept) before resolving. PASS (4 assertions)
3. Bucket escalation: AlbumArtist exhausts naturally (2 dead tracks, no
   ceiling hit), Artist tier resolves after exactly 1 observation there.
   PASS (3 assertions)
4. `BucketCeiling` enforcement: 3 dead AlbumArtist tracks fill the ceiling;
   a 4th track with real evidence is never reached — lands on
   `needs_review` despite better evidence existing one track later,
   confirming the ceiling is a real, enforced cap. PASS
5. Full exhaustion: dead tracks across all 3 buckets terminate cleanly at
   `needs_review`, no crash. PASS (2 assertions)
6. Real-world case, Gus Black (composer-only, single track — "Borrowed
   Time", *One Cell in the Sea*): candidate generation is entirely
   recording/performer-credit driven, so the only candidate generated is
   A Fine Frenzy (the real performer), not Gus Black. That spurious
   candidate is correctly rejected on a Poor name-similarity match
   (0.077 computed similarity), landing on `needs_review`, confidence
   0.12. Confirms a real, structural gap — composer-only candidate
   generation isn't built (Phase 3 territory) — while confirming the
   existing name-mismatch safety net prevents a false accept. PASS
   (3 assertions)
7. Real-world case, Queen ("Bohemian Rhapsody" on the non-canonical "Top
   2000 (Radio 2)" compilation, Artist-tier not AlbumArtist since "Various
   Artists" holds that role): correct candidate (Queen) is generated and
   identified, static NearExact (confidence 0.82), but no composer/writer-
   type relationship applies to a performer-only band credit — lands on
   `needs_review`, exposing the missing performer/recording-tier evidence
   collector, unrelated to the odd album metadata. PASS (2 assertions)
8. Real-world case, Florence + the Machine naming variants ("Florence and
   the machine" / "Florence & The Machine" / "Florence + The machine",
   no track evidence by design, isolating pure name-matching): classified
   Close (0.875), NearExact (0.955), and NearExact (1.000) respectively —
   exact figures computed by hand against the real Levenshtein algorithm
   before writing the assertions, then confirmed by the actual run.
   Verified against MusicBrainz's own listing: its sort-name for this
   artist is literally "Florence and the Machine". PASS (9 assertions)
9. Real-world case, Del Serino (composer alias, "That's It, I Quit, I'm
   Moving On" on Adele's *19*): same structural gap as Gus Black — Adele
   surfaces as the spurious candidate, correctly rejected on a Poor name
   match, confidence 0.12. Does not yet independently demonstrate the
   alias-resolution problem (that layer isn't reachable until
   composer-candidate-generation exists) — kept in the pot to
   differentiate from Gus Black once Phase 3 lands. PASS (2 assertions)

All 34 assertions passed in this unit.

**Not yet done, still Phase 2 scope:** `AliasEvidenceCollector`,
`RecordingRelationshipEvidenceCollector`, `AlbumMatchEvidenceCollector`,
`CorroborationTierEvidenceCollector`, `BayesianBeliefScorer` (still
`SimpleWeightedSumScorer` standing in), `EvidenceTranslator`,
`JointEvidenceRules`, `ApiCacheRepository`. Reproducing §18's worked
example exactly still depends on the album-match/corroboration-tier
collectors above.

## Phase 3–5
Not started.


Classes by tree (tagged by progress)
MetadataHealthCheck.v2/                                    (real plugin source - netstandard2.0)
├── Core/
│   ├── Model/                              Completed (Phase 1 scope + IObservationUnit, BucketCeiling/RoleWeights, Phase 2)
│   ├── Interfaces/                         Completed (+ IObservationEvidenceCollector, IObservationUnitProvider, Phase 2)
│   └── Engine/
│       ├── ResolutionEngine.cs              Completed (delegates to SequentialSampler, Phase 2)
│       └── SequentialSampler.cs             Completed (§5.5, Phase 2) — entity-agnostic, no Emby/MusicBrainz-specific knowledge
├── Sources/Emby/
│   ├── EmbyArtist.cs                       Completed
│   ├── EmbyArtistProvider.cs               Completed (ArtistFilter split fixed for netstandard2.0 compat, 2026-07-10)
│   ├── EmbyTrackObservationUnit.cs          Completed (new, Phase 2)
│   ├── EmbyArtistObservationUnitProvider.cs Completed (new, Phase 2) — 2 of §5.5.1's 4 distance-seeking rules implemented against real data; other 2 need data EmbyTrackCredit doesn't carry yet
│   └── IEmbyLibraryReader.cs               Placeholder (interface only — real ILibraryManager impl pending, needs live Emby SDK)
├── Resolvers/MusicBrainz/
│   ├── Client/IMusicBrainzApiClient.cs     Placeholder (interface only — real HTTP impl pending, needs live network)
│   ├── Strategies/AnchoredRecordingStrategy.cs   Completed (Strategy A)
│   ├── Strategies/SoftBucketStrategy.cs          Completed (Strategy B) — candidate generation is entirely recording/performer-credit driven; this is why composer-only artists can't be resolved yet, see Evidence Log 2026-07-11
│   ├── Strategies/AnchorByAssociationStrategy.cs  Not started (Strategy C, Phase 3) — confirmed necessary, not just planned, by real-world testing (Gus Black, Del Serino), 2026-07-11
│   ├── Evidence/NameDistanceEvidenceCollector.cs       Completed — verified against real-world naming variants (Florence + the Machine), 2026-07-11
│   ├── Evidence/WorkRelationshipEvidenceCollector.cs   Completed — converted to IObservationEvidenceCollector, Phase 2
│   ├── Evidence/AliasEvidenceCollector.cs              Not started (Phase 2)
│   ├── Evidence/RecordingRelationshipEvidenceCollector.cs  Not started (Phase 2) — confirmed necessary by real-world testing (Queen), 2026-07-11
│   ├── Evidence/AlbumMatchEvidenceCollector.cs         Not started (Phase 2)
│   └── Evidence/CorroborationTierEvidenceCollector.cs  Not started (Phase 2)
├── Scoring/
│   ├── SimpleWeightedSumScorer.cs          Completed (Phase 1 stand-in for BayesianBeliefScorer; applies RoleWeights multiplier as of Phase 2, currently neutral)
│   ├── ThresholdDecisionGate.cs            Completed
│   ├── EvidenceTranslator.cs               Not started (Phase 2, §5.6 plain-language layer)
│   └── JointEvidenceRules.cs                Not started (Phase 2)
├── Storage/
│   ├── Sqlite/BaseSqliteRepository.cs      Completed — ported from Emby.AutoOrganize's own source (Phase 1b)
│   ├── Sqlite/SqliteExtensions.cs          Completed — ported from Emby.AutoOrganize's own Data/SqliteExtensions.cs (added 2026-07-10; was missing, not part of the real SQLitePCL.pretty.core package)
│   ├── Sqlite/MatchRepository.cs           Completed (3 of §9.1's ~14 tables), uses confirmed reference CRUD idiom — real SQLite persistence unverified standalone, see Evidence Log 2026-07-10
│   ├── InMemoryIdentityCache.cs             Completed (Phase 1 stand-in; persistent version Phase 4)
│   ├── AnchorDependencyRepository.cs        Not started (Phase 3)
│   └── ApiCacheRepository.cs                Not started (Phase 2, api_cache table)
├── ActiveLearning/                          Not started (Phase 4)
├── Tasks/                                   Not started (Phase 5 — needs real IScheduledTask host)
├── Config/                                  Not started (Phase 5)
└── Diagnostics/StructuredLogger.cs           Completed (Console sink; real ILogger sink pending host access)

SQLitePCLPrettyShim/          SANDBOX-ONLY, not shipped — API-compatible test double for SQLitePCL.pretty.core (AI-assisted dev use only, not needed with real NuGet access)
SandboxValidation/            SANDBOX-ONLY, not shipped — compiles MetadataHealthCheck.v2's real source against the shim (AI-assisted dev use only)
SmokeTest/                    SANDBOX-ONLY, not shipped — manual-assertion console harness (xUnit blocked in AI sandbox, see Evidence Log). Real developer use: references MetadataHealthCheck.v2.csproj directly with real NuGet packages, using InMemoryMatchRepository.cs in place of real SQLite persistence.
├── Program.cs                              Completed — verified against real build 2026-07-10; 34 assertions (up from 8) verified against real build 2026-07-11 after Phase 2 additions
└── InMemoryMatchRepository.cs              Completed (added 2026-07-10) — fake IMatchRepository, bypasses unresolved real-SQLite provider version issue
```


Evidence Log
Learnings, insights, and observations from the project.

- **NuGet is unreachable in the build sandbox** (only a fixed domain allow-list,
  not including `api.nuget.org`, is reachable; `github.com` and related GitHub
  domains ARE reachable, which is how the reference repo itself was cloned).
  This blocks restoring `SQLitePCL.pretty.core`/`mediabrowser.server.core` for
  the real `netstandard2.0` project, and blocks xUnit for the test layer
  (§17.1). Resolved by keeping the real project's source and `.csproj` exactly
  as they should ship, and adding sandbox-only scaffolding
  (`SQLitePCLPrettyShim/`, `SandboxValidation/`) that compiles/runs the *same*
  source against a local API-compatible stand-in purely so it can be exercised
  here. **Action needed**: build `MetadataHealthCheck.v2.csproj` directly (not
  through the sandbox scaffolding) the first time this is done somewhere with
  normal NuGet access, and replace `SmokeTest`'s manual assertions with real
  xUnit at that point per §17.1.
- **Confirmed against the actual Emby.AutoOrganize source** (not re-derived
  from the spec's prose description of it): the shared-connection lifecycle
  where `Dispose()` (called implicitly by every `using (var connection = ...)`
  block throughout the codebase) must NOT close the underlying SQLite handle —
  only an explicit `.Close()` does, called once in `DisposeConnection()`. Our
  first shim draft got this wrong (closed on every `Dispose()`), which broke
  the second and all subsequent uses of the shared connection with a
  `SQLITE_MISUSE` error. Fixed once traced to this specific semantic. This is
  exactly the kind of detail that's easy to get wrong copying the *idea* of a
  pattern without reading the actual reference source, which is why the repo
  was cloned and read directly rather than working from memory/the spec's
  summary of it.
- **musicbrainz.org and the real Emby SDK are both unreachable here.** Neither
  the real `EmbyLibraryReader` (needs `ILibraryManager`, only available inside
  an actual Emby server host) nor the real MusicBrainz HTTP client can be
  built/tested in this sandbox. Both are behind clean interfaces
  (`IEmbyLibraryReader`, `IMusicBrainzApiClient`) specifically so the rest of
  the engine doesn't care which implementation is behind them — Phase 1's
  fixtures prove the pipeline logic works; the live implementations are a
  remaining task for an environment with that access.
- **Spec gap found**: §4's `Candidate` class has no `Id` field, but
  `EvidenceRecord.CandidateId` needs something to reference. Added `Candidate.Id`
  (and `Candidate.SourceEntityId`, also missing but needed by
  `resolution_candidates.source_entity_id` in §9.1's schema). Not a deviation
  from intent, just filling an evident omission — flagging here per the
  spec's own "if you need the rationale... a separate design-history document
  exists" framing, in case this was intentionally left for a specific reason
  not visible in the implementation spec.
- **Same-named-different-artist disambiguation needs Phase 2 evidence.** The
  §18 worked example's Y candidate is a literal same-name collision; Phase 1's
  only two evidence types (name similarity, work relationship) can't
  distinguish two candidates with identical names and no differing composer
  credit. The Phase 1 fixture uses a near-miss name for Y instead, to
  exercise what Phase 1 code actually does. This is not a bug — it's the
  expected reason album-match and corroboration-tier evidence exist, and
  it's why Phase 2's explicit goal is reproducing §18 exactly.
- **Fixture looseness observed during edge-case testing**: `FixtureMusicBrainzApiClient.SearchRecording`
  always returns its two canned recordings regardless of the queried track
  title (it doesn't simulate "no recording found at all"). This didn't
  invalidate the "unresolvable artist" test (the decision gate still
  correctly landed on `needs_review` on weak evidence) but is worth
  tightening if Phase 2 needs a true zero-candidate test case.
- **Emby cost model confirmed understood correctly per §8.1**: no HTTP round
  trip for Emby access in the real deployment (server-side plugin), so the
  fixture reader modeling a synchronous in-memory list is a faithful stand-in
  shape for the real thing, unlike the MusicBrainz client which really does
  need network I/O.
- **§5.5's pseudocode read ambiguously on a load-bearing point**: whether the
  sampler runs one candidate to completion in isolation, or evaluates all
  live candidates jointly per round. §18's own worked example only makes
  sense under the joint reading (the margin check needs every candidate at
  the same observation count). Confirmed this reading before building
  `SequentialSampler.cs`, not after — an isolated-per-candidate
  implementation would have been a plausible but wrong reading of the
  prose. Spec §5.5 updated to state this explicitly.
- **RoleWeights decision (confirmed, not to be relitigated)**: composer-tier
  evidence is NOT weighted down relative to AlbumArtist/Artist. Rationale:
  the difficulty with composer-only artists is entirely in generating them
  as a *candidate* at all (see next entry), not in the evidence being
  weaker once genuinely found — a real composer credit is exactly as
  strong a signal as a real AlbumArtist credit. `RoleWeights` stays neutral
  (1.0) across all three tiers by design, not as a placeholder awaiting
  tuning.
- **Major structural finding, confirmed by real-world testing (Gus Black,
  Del Serino, 2026-07-11): composer-only artists cannot be resolved as
  candidates at all today, regardless of name-matching or alias support.**
  Both `AnchoredRecordingStrategy` and `SoftBucketStrategy` generate
  candidates exclusively from a recording's performer credit
  (`MbRecordingResult.ArtistMbid`) — never from a work-relationship.
  `WorkRelationshipEvidenceCollector` only *adds evidence to* a candidate
  that already exists via that performer-credit path; it cannot create one
  from a composer/writer credit. Concretely: for an artist who is a pure
  composer with no recordings of their own as a performer, the only
  candidate ever generated from one of their composer-credited tracks is
  the track's real *performer* — a different person entirely. Confirmed
  end-to-end: Gus Black's "Borrowed Time" track correctly generates "A
  Fine Frenzy" as its sole candidate (the real performer), and Del
  Serino's Adele cover correctly generates "Adele". Both spurious
  candidates are then correctly rejected by `NameDistanceEvidenceCollector`
  (Poor match in both cases) rather than false-accepted — the existing
  safety net works — but Gus Black and Del Serino themselves are never
  even considered. This is exactly what Phase 3's Strategy C
  ("borrowed anchor") is for, and real-world testing has now confirmed
  it's a load-bearing gap, not a theoretical one. Del Serino's specific
  alias-resolution problem is a second, separate barrier stacked on top
  of this one — not independently testable until this first gap closes.
- **Second real-world gap confirmed: no recording/performer-tier evidence
  collector exists yet** (2026-07-11, Queen/"Bohemian Rhapsody" case).
  `WorkRelationshipEvidenceCollector` only recognizes writer/composer/
  lyricist/librettist relationships — a performer-only band credit
  correctly finds nothing, regardless of how well-known the artist is or
  how unusual the album metadata is (tested against a real non-canonical
  compilation, "Top 2000 (Radio 2)", to rule out album-related noise as
  the cause). This is `RecordingRelationshipEvidenceCollector`'s job,
  still not started.
- **ScoringConfig decision (confirmed, not to be relitigated)**: stays a
  plain C# class with hardcoded defaults, developer-edited and shipped via
  rebuild — not a database-backed, versioned, grid-editable settings page.
  Door deliberately left open to surface a subset of these values in a
  real settings UI once this ships as an actual Emby plugin with its own
  config-page infrastructure to ride on (§13) — at that point it's a small
  UI addition on existing infrastructure, not bespoke machinery built for
  this alone. Spec §10.3 updated to reflect this.
- **NameDistanceEvidenceCollector validated against real-world naming
  variance** (2026-07-11): confirmed via MusicBrainz's own listing that its
  sort-name for "Florence + the Machine" is literally "Florence and the
  Machine" — "and"/"&" aren't tagging errors, they're how MB itself refers
  to the artist. Three real-world taggings ("Florence and the machine",
  "Florence & The Machine", "Florence + The machine") classified Close,
  NearExact, NearExact respectively, matching hand-computed Levenshtein
  values exactly before the run confirmed them. No alias collector needed
  for this particular case — the existing distance metric already handles
  it.
Directives
Capture directives here

- Build in phased order per spec §21; update this log at the end of each unit of work after testing has confirmed state (this file's own standing instruction).
- v2 runs against its own SQLite database/config, entirely decoupled from the existing prototype (§19.1) — confirmed respected throughout.
- Emby plugin framework is netstandard2.0. SQLite approach is settled as matching the patterns used in the existing Emby plugin Emby.AutoOrganize (https://github.com/MediaBrowser/Emby.AutoOrganize) — confirmed by cloning and reading that repo directly, not re-derived independently.
- Do not guess Emby plugin framework details from the spec's prose description of it; read the actual reference source. The spec is a summary, not a substitute for the real source.


Build Log
What is done, what is in progress, and what is planned for the project.

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

## Phase 2–5
Not started.


Classes by tree (tagged by progress)

```
MetadataHealthCheck.v2/                                    (real plugin source - netstandard2.0)
├── Core/
│   ├── Model/                              Completed (Phase 1 scope)
│   ├── Interfaces/                         Completed
│   └── Engine/ResolutionEngine.cs          Completed (Phase 1 scope; sampler pending)
├── Sources/Emby/
│   ├── EmbyArtist.cs                       Completed
│   ├── EmbyArtistProvider.cs               Completed
│   └── IEmbyLibraryReader.cs               Placeholder (interface only — real ILibraryManager impl pending, needs live Emby SDK)
├── Resolvers/MusicBrainz/
│   ├── Client/IMusicBrainzApiClient.cs     Placeholder (interface only — real HTTP impl pending, needs live network)
│   ├── Strategies/AnchoredRecordingStrategy.cs   Completed (Strategy A)
│   ├── Strategies/SoftBucketStrategy.cs          Completed (Strategy B)
│   ├── Strategies/AnchorByAssociationStrategy.cs  Not started (Strategy C, Phase 3)
│   ├── Evidence/NameDistanceEvidenceCollector.cs       Completed
│   ├── Evidence/WorkRelationshipEvidenceCollector.cs   Completed
│   ├── Evidence/AliasEvidenceCollector.cs              Not started (Phase 2)
│   ├── Evidence/RecordingRelationshipEvidenceCollector.cs  Not started (Phase 2)
│   ├── Evidence/AlbumMatchEvidenceCollector.cs         Not started (Phase 2)
│   └── Evidence/CorroborationTierEvidenceCollector.cs  Not started (Phase 2)
├── Scoring/
│   ├── SimpleWeightedSumScorer.cs          Completed (Phase 1 stand-in for BayesianBeliefScorer)
│   ├── ThresholdDecisionGate.cs            Completed
│   ├── EvidenceTranslator.cs               Not started (Phase 2, §5.6 plain-language layer)
│   └── JointEvidenceRules.cs                Not started (Phase 2)
├── Storage/
│   ├── Sqlite/BaseSqliteRepository.cs      Completed — ported from Emby.AutoOrganize's own source (Phase 1b)
│   ├── Sqlite/MatchRepository.cs           Completed (3 of §9.1's ~14 tables), uses confirmed reference CRUD idiom
│   ├── InMemoryIdentityCache.cs             Completed (Phase 1 stand-in; persistent version Phase 4)
│   ├── AnchorDependencyRepository.cs        Not started (Phase 3)
│   └── ApiCacheRepository.cs                Not started (Phase 2, api_cache table)
├── ActiveLearning/                          Not started (Phase 4)
├── Tasks/                                   Not started (Phase 5 — needs real IScheduledTask host)
├── Config/                                  Not started (Phase 5)
└── Diagnostics/StructuredLogger.cs           Completed (Console sink; real ILogger sink pending host access)

SQLitePCLPrettyShim/          SANDBOX-ONLY, not shipped — API-compatible test double for SQLitePCL.pretty.core
SandboxValidation/            SANDBOX-ONLY, not shipped — compiles MetadataHealthCheck.v2's real source against the shim
SmokeTest/                    SANDBOX-ONLY, not shipped — manual-assertion console harness (xUnit blocked, see Evidence Log)
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

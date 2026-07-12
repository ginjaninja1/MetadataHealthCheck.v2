Directives
Capture directives here
- https://github.com/ginjaninja1/MetadataHealthCheck.v2
- Build in phased order per spec §21; update this log at the end of each unit of work after testing has confirmed state (this file's own standing instruction).
- v2 runs against its own SQLite database/config, entirely decoupled from the existing prototype (§19.1) — confirmed respected throughout.
- Emby plugin framework is netstandard2.0. SQLite approach is settled as matching the patterns used in the existing Emby plugin Emby.AutoOrganize (https://github.com/MediaBrowser/Emby.AutoOrganize) — confirmed by cloning and reading that repo directly, not re-derived independently.
- Do not guess Emby plugin framework details from the spec's prose description of it; read the actual reference source. The spec is a summary, not a substitute for the real source.
- **SETTLED 2026-07-12: candidate generation is artist-search-first, not recording-search-first.** Confirmed after a design review against real MusicBrainz data (see Evidence Log, 2026-07-12). §5.3 in `V2Specification.md` has been rewritten as the authoritative design; the old recording-first approach is preserved in the spec's Appendix A for reference only, in case a fundamental flaw in this direction is found later — it is not a live alternative to build against otherwise. **The code has NOT been updated to match yet** — `SoftBucketStrategy.cs` still implements the old, superseded recording-first approach as of this writing. See the "Coding checklist: artist-search-first candidate generation" entry below (2026-07-12) for the concrete, itemized list of what changing this involves — do not re-derive this from prose alone; the checklist is meant to be picked up directly, including by a fresh session with no other context.


Coding checklist: artist-search-first candidate generation (settled 2026-07-12, NOT YET IMPLEMENTED)
This is a concrete, itemized to-do list, not a summary — intended to be actionable by a fresh session with no other context. Read §5.3 and Appendix A of V2Specification.md first for the "why"; this is the "what."

1. **`Resolvers/MusicBrainz/Client/IMusicBrainzApiClient.cs` — model gap, confirmed real, needs fixing first:**
   `MbArtistResult` currently has NO `Aliases` field at all (only `Mbid`, `Name`, `Disambiguation`, `Score`). The real MusicBrainz `ws/2/artist` search response DOES return aliases inline (confirmed 2026-07-12 against real data — an `aliases` array per artist, no extra `inc=` needed). Add an `Aliases` list (or similar) to `MbArtistResult` before anything downstream can use it.
2. **`Fixtures/FixtureMusicBrainzApiClient.cs` — `SearchArtist` is currently dead-code-shaped:** it exists but nothing calls it, and its returned data (if any) has never been exercised by a real strategy. Once Strategy B is rewritten (below), every existing SmokeTest fixture case (Sarah Vaughan/§18, Gus Black, Queen, Florence ×3, Del Serino) will need real, meaningful `SearchArtist` fixture data added — including realistic near-miss/rival entries where the real-world case actually has one (e.g. nothing needed for Sarah Vaughan, since her real near-collisions don't clear a name-closeness bar anyway — see §18's corrected worked example).
3. **`Resolvers/MusicBrainz/Strategies/SoftBucketStrategy.cs` — the actual rework.** Needs to change from "search recordings, harvest every distinct ArtistMbid" to:
   a. Call `SearchArtist(source.DisplayName)` (§7.2 C1) to get a candidate-artist pool with names + aliases.
   b. Admit a candidate only if its name or an alias is a sufficiently close match to `source.DisplayName` — **this specific closeness test/threshold is an open sub-decision, not yet made.** Options include reusing `NameDistanceEvidenceCollector`'s existing Levenshtein-based logic (possible code reuse, not yet evaluated for fit) or a separate, simpler bar. Don't invent this unilaterally without flagging the choice.
   c. For each admitted candidate, confirm via the existing `RecordingLookup` (`Resolvers/MusicBrainz/Evidence/RecordingLookup.cs`, already built for evidence collection) as the corroborating signal — **reuse this class, don't build a second parallel recording-lookup implementation.**
   d. Naming: is `SoftBucketStrategy.cs`'s name/class still appropriate once its actual behavior is artist-search-first rather than a "soft," broad recording fallback? Open naming question, not decided — flagging rather than renaming unilaterally.
4. **`ScoringConfig`/`Core/Model/ScoringConfig.cs` — `CandidateGeneration.ArtistCandidateMinScore` (§5.4/§10.3) needs an actual, concrete implementation.** It's named in the spec today but not implemented anywhere in code. This is the real admission bar from step 3b above, whatever its final shape.
5. **Relationship to `AliasEvidenceCollector` (still not-started) — clarify before building, don't conflate:** candidate-generation-time alias checking (step 3b, an inclusion/admission decision) and `AliasEvidenceCollector`'s job (a per-candidate evidence *contribution* once a candidate already exists, §6.1's "Alias match, alone" / "Alias + recording match" LLR values) are complementary, not duplicative — they answer different questions ("should this candidate exist at all" vs. "how much does its alias match contribute to confidence"). Worth stating explicitly so they don't get built as the same thing by accident.
6. **Expect further `SmokeTest` assertion churn once this lands**, the same way Phase 2's evidence-collector work already changed several assertions twice — a single-candidate case (e.g. Queen, Gus Black) may no longer even generate a "wrong" candidate at all once admission happens before generation, which changes what the test can assert about candidate count/log lines, not just about final status.


Build Log
What is done, what is in progress, and what is planned for the project.


Status as of 2026-07-12 (design review)


Design review pass against real MusicBrainz data, followed by a settled
architecture decision — no code changed yet, but a firm direction is now
recorded (not just findings). Confirmed the §18/Sarah Vaughan fixture case
was entirely invented rather than grounded in real MB data (unlike Gus
Black/Queen/Florence) — §18 has since been rewritten using real data and
the new confirmed flow (see below). **Candidate generation is now settled
as artist-search-first, not recording-search-first** — today's code
(`SoftBucketStrategy.cs`) admits every distinct ArtistMbid a recording
search returns with no upfront name/alias gate and no relevance-score
filter, despite one being named in the spec; this is now confirmed as the
wrong direction and the fix (search by artist name+aliases first, use
recording-presence as confirmation) is settled, not just proposed — see
the Directives section and coding checklist at the top of this file for
the concrete, itemized implementation TODO, and V2Specification.md §5.3 +
Appendix A for the corrected/superseded design text. **Not yet
implemented in code.** Investigated a real same-name-artist collision
(Nirvana, US grunge vs. UK psych-pop) and found the current per-round
joint-candidate-evaluation design already handles it correctly, given
disjoint real catalogs — this part of the design needs no change.
Identified one real, deliberately-deferred residual risk: a generic track
title on a generic album title could survive the fallback ladder and pick
up genuine (not spurious) weak corroboration against the wrong same-named
candidate — proposed future fix recorded in §6.1, not built, no action
needed now.

Status as of 2026-07-12


Album-match precursor, corroboration-tier, and recording-relationship
evidence collectors built and integrated — closes out this Phase 2
increment's stated goal of reproducing §18's worked example, and closes
the Queen/performer-tier gap confirmed by real-world testing on
2026-07-11. A shared, memoized per-track recording lookup
(`RecordingLookup.cs`, new) replaces what would otherwise have been three
independent MusicBrainz recording searches per sampler round for the same
(candidate, track) pair — `WorkRelationshipEvidenceCollector` refactored
onto it, `RecordingRelationshipEvidenceCollector` and
`CorroborationTierEvidenceCollector` built onto it from the start. All 33
SmokeTest assertions pass, including 5 pre-existing assertions updated to
reflect richer evidence now correctly resolving cases that previously sat
at `needs_review` (Queen, and all three Florence + the Machine naming
variants) — see Evidence Log for the full reasoning on why those changed
outcomes are correct, not regressions.

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
`BayesianBeliefScorer` (still `SimpleWeightedSumScorer` standing in, now
also handling the one §5.2 supersession rule as a narrow special case —
see next entry), `EvidenceTranslator`, `JointEvidenceRules` (the general
`JointEvidencePairs` mechanism — not yet needed since only one specific
pair, §5.2's supersession, has been implemented so far), `ApiCacheRepository`
(deliberately deferred — see Evidence Log). §18's worked example is now
reproduced structurally (auto-accept, correct candidate, single
observation, correct margin), though not with identical absolute LLR
numbers to the spec's own illustrative math — see Evidence Log.

## Phase 2 continued — Album-Match / Corroboration-Tier / Recording-Relationship evidence — this increment DONE, tested 2026-07-12

Scope of this increment: the three remaining evidence collectors flagged
as "not yet done" above, motivated directly by the prior increment's two
real-world findings (Queen's missing performer-tier path; §18's
reproduction depending on album-match/corroboration-tier). `AliasEvidenceCollector`
and `BayesianBeliefScorer` remain out of scope — see updated "not yet
done" list below.

**Design alignment reached before coding (see Evidence Log for full
reasoning on each):**
- A unified per-track recording lookup (fallback ladder: track+artist+album
  → track+album → track alone) replaces independent `SearchRecording` calls
  per collector — confirmed before building any of the three new collectors,
  not after finding the duplication by accident.
- `api_cache`/`ApiCacheRepository` deliberately deferred rather than built
  alongside this — building the cache key shape before the real lookup
  shape was settled would have meant guessing.
- §5.2's supersession rule resolved at scoring time, in
  `SimpleWeightedSumScorer`, not at collection time as first proposed —
  corrected once `SequentialSampler.cs`'s actual Step 1/Step 2 ordering was
  re-examined closely (the album-match precursor always runs before any
  per-track observation exists, so it can never itself know whether a later
  Tier 1 observation will arrive for the same album).

**What was built:**
- Resolvers/MusicBrainz/Evidence/`RecordingLookup.cs` (new) — shared,
  memoized per-(candidate, track) recording lookup implementing the
  fallback ladder above. Records which rung produced a hit (`RungReached`,
  diagnostic-only for now — not yet exercised by any fixture case, since
  every existing SearchRecording branch matches on track title alone
  regardless of the artist-name parameter; an honest coverage gap, not
  claimed as validated).
- Resolvers/MusicBrainz/Client/`IMusicBrainzApiClient.cs` — `SearchRecording`
  signature extended with an `artistName` parameter to support the ladder.
  `AnchoredRecordingStrategy.cs`/`SoftBucketStrategy.cs` call sites updated
  to match (both pass `null` for now — widening candidate-generation
  strategies onto the ladder themselves is follow-up work, out of this
  evidence-collector-focused unit's scope).
- Resolvers/MusicBrainz/Evidence/`WorkRelationshipEvidenceCollector.cs` —
  refactored onto `RecordingLookup` instead of its own direct
  `SearchRecording` call (previously flagged in its own comments as a
  "revisit if this proves too indirect" stand-in — now revisited).
- Resolvers/MusicBrainz/Evidence/`RecordingRelationshipEvidenceCollector.cs`
  (new) — recording-level relationships (producer/arranger), sourced from
  the same underlying call as `WorkRelationshipEvidenceCollector`'s
  work-rels, filtered differently (§5.4/§7.2 C5).
- Resolvers/MusicBrainz/Evidence/`CorroborationTierEvidenceCollector.cs`
  (new) — Tier 1/2/3 per §6.3, using `MbRecordingResult.TrackTitleMatches`/
  `ReleaseTitleMatches`, already present on the fixture's model.
- Resolvers/MusicBrainz/Evidence/`AlbumMatchEvidenceCollector.cs` (new) —
  static album-match precursor per §5.2, once per candidate.
- Core/Model/`ScoringConfig.cs` — added `AlbumMatch.Distinctive/Generic`,
  `CorroborationTier.Tier1/2/3`, `RecordingRelationship.Producer/Arranger`
  evidence weights per §6.1.
- Scoring/`SimpleWeightedSumScorer.cs` — added the §5.2 supersession
  filter: a `CorroborationTier.Tier1` record for a given `AlbumId` drops
  any `AlbumMatch.*` record sharing that same `AlbumId` before summing.
- Resolvers/MusicBrainz/`MusicBrainzArtistResolverPlugin.cs` — wires all
  three new collectors and the shared `RecordingLookup` instance.
- Fixtures/`FixtureMusicBrainzApiClient.cs` — `SearchRecording` signature
  updated to match; added a real-world producer credit for Queen
  ("Bohemian Rhapsody" co-produced by Roy Thomas Baker and Queen
  themselves, externally corroborated) giving `RecordingRelationshipEvidenceCollector`
  a genuine case to fire on; added a synthetic Tier-3-only recording
  ("A Loosely Related Track") needed to keep the multi-observation sampler
  test meaningful once static evidence + any real corroboration started
  crossing the accept threshold on the first useful observation alone.
- SandboxValidation/`SandboxValidation.csproj` — **pre-existing bug fixed,
  unrelated to this unit's actual scope**: its `Compile Include` glob
  (`../MetadataHealthCheck.v2/**/*.cs`) assumed source lived in a nested
  subfolder that doesn't exist in the current repo layout (source is at
  the repo root, sibling to `SandboxValidation/`) — matched zero files.
  Fixed to include the real sibling folders explicitly. Also excludes
  `Storage/Sqlite/*` for now (needs real SQLitePCL.pretty.core API surface
  the local shim doesn't implement — pre-existing gap, not exercised by
  `SmokeTest` either way since it uses `InMemoryMatchRepository`).

**Testing performed (SmokeTest console app, `dotnet run`):**
1. §18 worked-example reproduction (the existing Phase 1 fixture case IS
   §18's exact setup): now resolves with `AlbumMatch`/`CorroborationTier`
   evidence contributing as designed. Structurally matches §18 (auto-accept,
   correct candidate, single observation, margin 7.0 nats matching §18's
   stated figure). Absolute per-candidate LLR values do NOT match §18's own
   illustrative math — see Evidence Log for the two reasons found, both
   flagged rather than silently forced to match. PASS (2 new assertions)
2. Multi-track adaptive stop (pre-existing test, fixture updated): needed
   a genuinely weak (Tier 3) first observation to still require 2 draws,
   since almost any real corroboration now crosses the accept threshold
   on the first useful observation given the higher static baseline
   (name-similarity + album-match precursor). PASS (4 assertions,
   unchanged pass/fail shape, updated fixture)
3. Queen real-world case (pre-existing test, assertions updated): now
   resolves `auto_accept` via the new producer credit
   (`RecordingRelationship.Producer`, +0.8) plus `CorroborationTier.Tier2`
   (+1.8) on top of existing name-similarity (+1.5) = 4.1 nats, crossing
   the +4.0 threshold. This is the fix this fixture case was built to
   motivate, not a regression. PASS (2 assertions, updated from
   `needs_review` to `auto_accept`)
4. Florence + the Machine naming variants (pre-existing test, assertions
   updated): all three now resolve `auto_accept` — `CorroborationTier.Tier1`
   correctly fires for the genuine "Dog Days Are Over"/"Lungs" full-triple
   match, which didn't exist as an evidence type when this test was
   originally written to isolate pure name-matching. The name-similarity
   bucket assertions (the original point of the test) are unchanged and
   still pass. PASS (9 assertions, 3 of 9 updated from `needs_review` to
   `auto_accept`)
5. Gus Black, bucket escalation, `BucketCeiling` enforcement, full
   exhaustion, Del Serino: unaffected by this increment's changes, all
   still pass unchanged.

All 33 assertions passed in this unit (down from 34 — the multi-track
adaptive-stop test's fixture was replaced, not added to; assertion count
per scenario is otherwise unchanged or increased).

**Not yet done, still Phase 2 scope:** `AliasEvidenceCollector`,
`BayesianBeliefScorer` (its remaining job beyond what
`SimpleWeightedSumScorer` now covers is the *general* `JointEvidencePairs`
mechanism — alias+recording joint evidence specifically needs
`AliasEvidenceCollector` to exist first), `EvidenceTranslator`,
`ApiCacheRepository` (deliberately deferred, see Evidence Log). The
tier→rung-priority hypothesis in `RecordingLookup.cs`'s own comments
(which name field leads the ladder, by tier) is unverified — flagged
in-file, not assumed correct.

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
│   ├── Client/IMusicBrainzApiClient.cs     Placeholder (interface only — real HTTP impl pending, needs live network; SearchRecording extended with artistName param, Phase 2 cont'd)
│   ├── Strategies/AnchoredRecordingStrategy.cs   Completed (Strategy A)
│   ├── Strategies/SoftBucketStrategy.cs          Completed for the superseded recording-first design (Strategy B) — **PENDING REWORK, not yet done**: artist-search-first is the settled direction as of 2026-07-12 (see Directives + coding checklist at top of this file, and V2Specification.md §5.3/Appendix A); this file still implements the old approach
│   ├── Strategies/AnchorByAssociationStrategy.cs  Not started (Strategy C, Phase 3) — confirmed necessary, not just planned, by real-world testing (Gus Black, Del Serino), 2026-07-11
│   ├── Evidence/RecordingLookup.cs                     Completed (new, Phase 2 cont'd) — shared memoized per-(candidate,track) recording lookup, fallback ladder; RungReached tracked but not yet exercised by any fixture
│   ├── Evidence/NameDistanceEvidenceCollector.cs       Completed — verified against real-world naming variants (Florence + the Machine), 2026-07-11
│   ├── Evidence/WorkRelationshipEvidenceCollector.cs   Completed — converted to IObservationEvidenceCollector, Phase 2; refactored onto RecordingLookup, Phase 2 cont'd
│   ├── Evidence/AliasEvidenceCollector.cs              Not started (Phase 2)
│   ├── Evidence/RecordingRelationshipEvidenceCollector.cs  Completed (new, Phase 2 cont'd) — verified against real-world case (Queen/"Bohemian Rhapsody" producer credit), 2026-07-12
│   ├── Evidence/AlbumMatchEvidenceCollector.cs         Completed (new, Phase 2 cont'd) — static precursor, §5.2
│   └── Evidence/CorroborationTierEvidenceCollector.cs  Completed (new, Phase 2 cont'd) — Tier 1/2/3, §6.3
├── Scoring/
│   ├── SimpleWeightedSumScorer.cs          Completed (Phase 1 stand-in for BayesianBeliefScorer; applies RoleWeights multiplier as of Phase 2, currently neutral; applies §5.2 supersession filter as of Phase 2 cont'd — one narrow joint-evidence rule, not the general mechanism below)
│   ├── ThresholdDecisionGate.cs            Completed
│   ├── EvidenceTranslator.cs               Not started (Phase 2, §5.6 plain-language layer)
│   └── JointEvidenceRules.cs                Not started (Phase 2) — general JointEvidencePairs mechanism; §5.2's specific supersession case already handled ahead of this, in SimpleWeightedSumScorer
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
SandboxValidation/            SANDBOX-ONLY, not shipped — compiles MetadataHealthCheck.v2's real source against the shim (AI-assisted dev use only). Compile-Include glob path bug fixed 2026-07-12 (was matching zero files against the current flat repo layout); Storage/Sqlite excluded pending real SQLitePCL.pretty.core API surface in the shim.
SmokeTest/                    SANDBOX-ONLY, not shipped — manual-assertion console harness (xUnit blocked in AI sandbox, see Evidence Log). Real developer use: references MetadataHealthCheck.v2.csproj directly with real NuGet packages, using InMemoryMatchRepository.cs in place of real SQLite persistence. Locally re-pointed at SandboxValidation.csproj for this session's testing only (nuget.org unreachable again this session) — not a repo change, .csproj as committed still references the real project directly.
├── Program.cs                              Completed — verified against real build 2026-07-10; 34 assertions (up from 8) verified against real build 2026-07-11 after Phase 2 additions; 33 assertions verified 2026-07-12 after Phase 2 cont'd (5 pre-existing assertions updated to reflect richer evidence now correctly resolving previously under-evidenced cases)
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
- **Recording-lookup duplication identified and resolved before, not
  after, building the three new collectors** (2026-07-12):
  `WorkRelationshipEvidenceCollector` already had its own `SearchRecording`
  call, and `RecordingRelationshipEvidenceCollector`/
  `CorroborationTierEvidenceCollector` would each have needed the same
  (candidate, track) lookup independently — three MusicBrainz recording
  searches per sampler round for the same underlying fact. Resolved with a
  shared, memoized `RecordingLookup` class instead of touching
  `SequentialSampler.cs` or the `IObservationEvidenceCollector` interface —
  all three collectors take the same `RecordingLookup` instance, constructed
  once in `MusicBrainzArtistResolverPlugin`, so only the first collector to
  ask for a given (candidate, track) pair in a round actually triggers a
  search.
- **Fallback ladder introduced for recording lookups, deliberately not
  solving the "wrong-field-hurts" tension** (2026-07-12): trackname+
  artistname+albumname → trackname+albumname → trackname alone. Confirmed:
  there is no way to avoid the risk that a wrong album or wrong artist-name
  text degrades a fallback rung's result quality — both were considered and
  neither can be designed around cleanly. Accepted as a known,
  low-probability-in-practice risk rather than solved, relying on
  `NameDistanceEvidenceCollector`'s existing rejection of poor name matches
  as the real safety net (already proven against exactly this shape of risk
  by the Gus Black/Del Serino cases, where a spurious candidate is
  correctly rejected despite genuine Tier 1 corroboration existing under a
  wrong candidate's MBID). `RungReached` is recorded on every lookup result
  as diagnostic-only data, intended to eventually feed the §16 calibration
  job's own kind of question ("is it ever worth falling back to
  trackname-alone, or never?") — not yet exercised by any fixture case,
  since none of the current `SearchRecording` fixture branches vary their
  result by the `artistName` parameter. Honest gap, flagged in
  `RecordingLookup.cs` itself, not fabricated coverage.
- **§5.2 supersession rule: design corrected mid-session, before
  implementation, not after** (2026-07-12): first proposed as a
  collection-time decision (skip re-emitting the album-match precursor for
  an album a track-level lookup has already resolved), then corrected on
  closer reading of `SequentialSampler.cs`'s actual Step 1/Step 2 ordering
  — `AlbumMatchEvidenceCollector` runs once, always before any per-track
  observation exists, so it has no way to know in advance whether a later
  observation will produce a Tier 1 record for the same album. Implemented
  at scoring time instead, in `SimpleWeightedSumScorer`, as a narrow,
  explicit filter (not the general `JointEvidencePairs` mechanism, which
  stays not-started).
- **§18's worked example reproduced structurally, not numerically exactly**
  (2026-07-12): the existing Phase 1 fixture case (Sarah Vaughan, single
  "Autumn Leaves"/"Crazy and Mixed Up" track) is §18's exact setup, and now
  resolves auto-accept, correct candidate, single observation, with a 7.0
  nat margin matching §18's own stated figure. The per-candidate absolute
  LLR values do NOT match §18's illustrative math, for two reasons found
  during this unit, both worth a second look rather than silently
  accepted: (1) §18's prose never mentions a `WorkRelationship` credit, but
  this fixture's "Autumn Leaves" genuinely carries one for the correct
  candidate (+2.5), pushing its real total (7.5) above §18's stated 5.0;
  (2) §18 assumes the runner-up candidate receives a −2.0 penalty for "no
  track/album match", but no negative-evidence type for this exists
  anywhere in §6.1's catalog — the fixture's runner-up recording is
  legitimately attributed to the runner-up's own MBID, just weakly (Tier 3,
  neither title field corroborates), giving it a small *positive* +0.5
  instead. The 7.0 nat margin matching §18 anyway is confirmed coincidental
  (both totals moved up from the illustrative figures by a similar amount),
  not evidence the underlying math matches §18's intent point-for-point.
  Open question for a design discussion, not resolved in this unit: should
  §18's prose be corrected to match what was actually built, or should a
  genuine "no-match negative evidence" type be added for a candidate whose
  recording search under a given track title resolves to a *different*
  artist entirely?
- **`api_cache`/`ApiCacheRepository` deliberately deferred, not
  overlooked** (2026-07-12): building the cache key/table structure before
  the real MusicBrainz lookup shape was settled (the fallback ladder in
  `RecordingLookup.cs`) would have meant guessing at what's actually being
  cached. Revisit once the ladder's real query shapes are exercised against
  more than fixture data.
- **Pre-existing bug found in `SandboxValidation.csproj`, unrelated to this
  unit's actual scope, fixed in passing** (2026-07-12): its `Compile
  Include` glob path assumed a nested `MetadataHealthCheck.v2/` subfolder
  that doesn't exist in the current repo layout — the real source sits at
  the repo root as a sibling of `SandboxValidation/`. This meant the glob
  silently matched zero files; the project "built" successfully with an
  empty compile set. Found while setting up a way to actually run
  `SmokeTest` in this session (`nuget.org` unreachable again this session —
  same root cause as the entry above from 2026-07-08, evidently
  session-dependent rather than permanently resolved). Fixed by listing the
  real sibling folders explicitly.

**Design review against real MusicBrainz data (2026-07-12, no code changed
in this pass — analysis/design findings only, captured here per this log's
own remit to record "learnings, insights, and observations," not just
tested code):**

- **The §18/Sarah Vaughan fixture case was confirmed to be entirely
  invented, not grounded in any real MusicBrainz query — an important
  distinction from Gus Black/Queen/Florence, which are real facts even
  where the exact MBID strings weren't independently confirmed.** Real
  `ws/2/artist` and `ws/2/recording` lookups were pulled (both via
  Claude's own web search/fetch, and directly by the user against the live
  API) for the real "Sarah Vaughan" / "Autumn Leaves" / "Crazy and Mixed
  Up" case. Findings: MusicBrainz's real MBID for the correct artist is
  `351d8bdf-33a1-45e2-8c04-c85fad20da55`; her real registered aliases
  (`Sarah Vahghan`, `Sarah Voughan`, `Sara Vaughan`, `Vaughan Sarah`,
  `Sarah Vaughn`) are all spelling variants of the *same* person, not a
  distinct rival artist — meaning the existing fixture's "Y" candidate
  (invented as `GetArtistDisplayName` → `"Sara Vaughn"`, framed as a
  competing same-named artist) doesn't correspond to any real ambiguity.
  The real near-collisions that do exist for this name
  (`Sarah Vaughan and Her Trio`, `...and Her Octet`, `...and The All Star
  Band` — distinct MB entities, lower relevance scores, clearly
  differentiated full names) aren't a hard case either. **Conclusion: this
  specific fixture, however useful for exercising the code's own logic
  (§18 reproduction), was never a real test of whether the engine could
  be misled — it can't be used to answer that question, and shouldn't be
  cited as evidence either way for real-world efficacy.**
- **Confirmed via the real MusicBrainz response: `SearchArtist` /
  alias-based candidate generation is fully dead code today.** Neither
  `AnchoredRecordingStrategy` nor `SoftBucketStrategy` calls it — candidate
  generation is 100% recording-search-driven (§5.3's Strategy A/B), and
  aliases are never fetched or compared anywhere in the pipeline (no code
  path reads them, `AliasEvidenceCollector` remains not-started). The real
  `ws/2/recording` response confirmed that MusicBrainz's own recording
  search results already carry the artist's registered aliases inline
  (`artist-credit[].artist.aliases`), with no extra `inc=` needed — a real,
  observed data point (not yet confirmed as guaranteed across all query
  shapes) that some of what `AliasEvidenceCollector`/`NameDistanceEvidenceCollector`
  need may already be available for free from calls the pipeline already
  makes.
- **A real, structural candidate-generation gap identified and discussed
  in depth — SETTLED 2026-07-12 (see Directives at top of this file and
  the "Coding checklist" entry immediately below Directives):** current
  candidate generation harvests every distinct `ArtistMbid` a recording
  search returns and admits all of them as full candidates unconditionally
  — there is no artist-name/alias gate applied first, and
  `ScoringConfig.CandidateGeneration.ArtistCandidateMinScore` (already
  named in §5.4/§10.3 as the intended relevance-score admission filter) is
  not actually implemented anywhere in `SoftBucketStrategy` today. An
  alternative architecture was proposed and discussed at length: search by
  artist name (+ aliases) *first*, generate candidates only from that
  name-matched pool, then check for that specific candidate's presence in
  the recording data as the corroborating signal — inverting today's
  "recording first, name-check after" flow to "name first,
  recording-presence as confirmation." Real trade-off surfaced on both
  sides: today's design risks admitting spurious candidates it must then
  rely on negative name-similarity to cancel out after the fact; the
  proposed design risks *excluding* a correct candidate upfront if the
  real recording's artist-credit doesn't textually resemble the Emby-tagged
  name and isn't in MusicBrainz's own registered alias list (a genuine MB
  data-completeness gap, not hypothetical). **Decided: artist-search-first
  is the confirmed direction** — accepted deliberately, on the basis that a
  ground-truth Emby observation gives something real to search against,
  and ground-truth accuracy is the user's responsibility, not the engine's
  to solve for. The old approach is kept in the spec's Appendix A rather
  than deleted, specifically in case a fundamental flaw in the new
  direction is found later. **Not yet implemented in code** — see the
  coding checklist. Confirmed as a concrete instance of the old design's
  problem: `RecordingLookup`'s own rung 2 (trackname+albumname) is a
  byte-for-byte duplicate of `SoftBucketStrategy`'s own candidate-generation
  call for the same track — traced against real, literal `ws/2` URLs, not
  just asserted in the abstract.
- **Same-name-different-artist collision (real MusicBrainz case: "Nirvana"
  — 90s US grunge band vs. 1967 UK psychedelic pop duo) investigated and
  found NOT to be a real opportunity for the engine to be misled, given
  the two acts' catalogs are entirely disjoint (verified: no shared track
  titles between either act's real discography).** Checking an observed
  track against both same-named candidates (per the sampler's existing
  joint-evaluation design) correctly produces no signal for the wrong
  candidate and strong signal for the right one — the design handles this
  correctly as built, no change needed. This was a useful negative result:
  confirms the joint per-round evaluation approach is sound for genuine
  same-name collisions, provided the observed track itself carries a
  distinctive title.
- **Real, valid residual risk identified and deliberately deferred, not
  dismissed:** if a same-named-artist collision case involved a **generic**
  track title (e.g. a song called "Love") on a **generic** album title
  (e.g. "Greatest Hits" or a close variant) — rather than a distinctive
  title like "Smells Like Teen Spirit" — the fallback ladder could
  plausibly relax to a loose rung and pick up a real, non-spurious
  corroboration hit against the *wrong* same-named artist, since neither
  the track nor album title is distinctive enough on its own to rule that
  candidate out. This is a genuine gap, structurally different from the
  Nirvana case above, and needs a deliberately generic-titled real example
  to test properly (not yet done). **Proposed future tweak (not built,
  Phase 2+/later):** extend the existing distinctive/generic title
  treatment already built for `AlbumMatchEvidenceCollector` (§6.1's
  `AlbumMatch.Distinctive` vs `AlbumMatch.Generic` split) to also apply
  within `CorroborationTierEvidenceCollector`'s own weighting — i.e.
  downweight a Tier 1/2/3 hit whose track and/or album title is itself
  generic, and correspondingly upweight longer/more distinctive track
  titles. Track duration was also raised as a possible additional signal
  for large-distance elimination (e.g. a "Love" hit that's 7 minutes long
  when the real observed track is 3 minutes) — not currently possible,
  since `EmbyTrackCredit` carries no duration field at all today (§4's
  model doesn't have one).
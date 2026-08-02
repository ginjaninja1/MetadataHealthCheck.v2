## Architecture: Three Layers of Authority

This system has three layers. Each has authority over a different thing, and
none of them should assume knowledge that belongs to another.

### Layer 1 — Engine (`Core/Engine/ResolutionEngine<TSourceEntity,TConfig>`)

Owns exactly the mechanics that are true for *any* resolver, regardless of
domain:
- identity cache check (skip work already done)
- ask the resolver's `Strategies` for candidates, persist them
- delegate to the resolver's own `IResolutionStrategy` to produce a `MatchResult`
- apply `ForcedReviewSignal` override, persist the result, update identity
  cache on auto-accept

Engine has **no concept of** buckets, observation units, rounds, sequential
sampling, or early stopping. Those are one resolver's chosen approach to
turning evidence into a decision -- not a fact about resolution in general.
`SequentialSampler` is *an* `IResolutionStrategy` implementation, not *the*
mechanism Engine provides. A resolver with no unit/round concept at all
supplies a different, simpler `IResolutionStrategy` and Engine never needs to
know sequential sampling exists.

### Layer 2 — Resolver (e.g. `Resolvers/Artist/MusicBrainz/`)

Owns everything domain-specific: candidate generation strategies, what an
observation unit is (if any), evidence collector shapes, bucket ordering,
scoring, the decision gate, and which `IResolutionStrategy` it wants to use.
Different resolvers can look completely different at this layer with zero
impact on Engine. Nothing here should ever require an Engine change.

### Layer 3 — Orchestrator (not yet built)

Owns things no single resolver or Engine instance can know on its own:
- **ordering across entity types** -- e.g. resolve one entity type before
  another because the second's evidence collection can be corroborated by
  the first's already-persisted results
- **cross-resolver information flow** -- one resolver using another
  resolver's already-persisted `MatchResult`/identity-cache entry as an input
  to its own candidate generation or evidence collection. This flows through
  shared persisted state (`IMatchRepository`, `IIdentityCache`), never
  through one resolver calling another resolver directly.
- **human-in-the-loop surface** -- consuming `needs_review` results from any
  resolver uniformly, since `MatchResult` is already resolver-agnostic

### Authority direction

**Orchestrator -> Resolver -> Engine.** Engine is a shared tool the resolver
uses for repetitive bookkeeping (cache, persistence). It does not dictate
strategy to the resolver. The resolver dictates its own strategy and supplies
it to Engine. The orchestrator dictates sequencing and cross-resolver context
that neither Engine nor any single resolver can know by itself.

### Test case for this split

A second resolver (e.g. AudioDB-Artist) should be able to:
- reuse `SequentialSampler` if its shape genuinely fits, or supply a
  completely different `IResolutionStrategy` if it doesn't -- without
  touching Engine either way
- read another resolver's already-persisted results via `IMatchRepository`/
  `IIdentityCache`, without adding a direct dependency on that resolver's code

If either of those requires an Engine change, the layering has failed.
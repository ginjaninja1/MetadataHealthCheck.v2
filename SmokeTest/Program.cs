using MetadataHealthCheck.v2.Core.Engine;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Diagnostics;
using MetadataHealthCheck.v2.Fixtures;
using MetadataHealthCheck.v2.Resolvers.MusicBrainz;
using MetadataHealthCheck.v2.Resolvers.MusicBrainz.Client;
using MetadataHealthCheck.v2.Scoring;
using MetadataHealthCheck.v2.Sources.Emby;
using MetadataHealthCheck.v2.Storage;
using SmokeTest;

// LIVE MusicBrainz run against real observation data (SmokeTest/Observations.txt).
// HttpMusicBrainzApiClient (Resolvers/MusicBrainz/Client/) hits
// https://musicbrainz.emby.tv/ws/2/ -- no throttling, no User-Agent, no API key.
// This file's only job is composing plugin + engine, feeding it real observations,
// and reporting -- it has no opinion on how the client works internally.
//
// REWRITTEN 2026-07-16 (readability pass): previously this printed only a single
// "==> status=... target=... confidence=... margin=..." line per artist, with the
// engine's internal Debug/Info log lines scrolling past ungrouped. That told you
// the answer but not how the engine got there. Now each artist gets:
//   1. An OBSERVATIONS block -- what was actually loaded (tracks/roles/albums)
//      before any resolution happens, so you can eyeball "does this look right"
//      before watching the engine work on it.
//   2. Stage banners around the engine's own live trace (MbApi lookups, candidate
//      generation, sampler evidence draws) -- same log lines as before, just
//      visually grouped instead of an undifferentiated scroll.
//   3. A SCOREBOARD -- every candidate the engine generated, each one's final
//      evidence tally and LLR/confidence, ranked. This is reconstructed AFTER
//      ResolveOne returns, from InMemoryMatchRepository's own saved candidates/
//      evidence (exposed for exactly this) run back through SimpleWeightedSumScorer
//      (a plain, side-effect-free class) -- no engine or sampler code changed to
//      get this; it's a read of what the engine already recorded.
//   4. A DECISION block that states the outcome against the actual configured
//      thresholds, not just the bare status string.
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== MetadataHealthCheck.v2 SmokeTest: LIVE MusicBrainz run ===");
Console.WriteLine("Real observation data (SmokeTest/Observations.txt), real engine, and REAL");
Console.WriteLine("MusicBrainz data via https://musicbrainz.emby.tv/ws/2/ (HttpMusicBrainzApiClient).");
Console.WriteLine("Uses an in-memory IMatchRepository, not real SQLite -- see InMemoryMatchRepository.cs.\n");

var logger = new StructuredLogger();
var mbClient = new HttpMusicBrainzApiClient(logger);
var identityCache = new InMemoryIdentityCache();
var scoringConfig = new ScoringConfig();
var scorer = new SimpleWeightedSumScorer(); // reused post-hoc for the scoreboard, not part of resolution itself

var plugin = new MusicBrainzArtistResolverPlugin(mbClient, identityCache, scoringConfig, logger);

Banner("STAGE: evidence/weight consistency check");
var configFindings = EvidenceConfigValidator.Validate(plugin.EvidenceCollectors, plugin.ObservationEvidenceCollectors, plugin.RoundBasedObservationEvidenceCollectors, scoringConfig.EvidenceWeights);
if (configFindings.Count == 0)
{
    Console.WriteLine("No issues found: every ScoringConfig.EvidenceWeights entry is declared by some registered collector, and every declared weighted evidence type has a matching weight entry.\n");
}
else
{
    foreach (var f in configFindings)
        Console.WriteLine($"[{f.Severity}] {f.Detail}");
    Console.WriteLine();
}

var observationsPath = Path.Combine(AppContext.BaseDirectory, "observations.txt");
if (!File.Exists(observationsPath))
    observationsPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "observations.txt");
if (!File.Exists(observationsPath))
{
    Console.WriteLine("FATAL: could not find observations.txt (looked next to the executable and at the project source directory).");
    return 1;
}

var emby = new TextFileEmbyLibraryReader(observationsPath);
var provider = new EmbyArtistProvider(emby);
var context = new ResolutionContext();

var artists = provider.GetAll(context).ToList();
Console.WriteLine($"Loaded {artists.Count} artist(s) from {observationsPath}.\n");
Console.WriteLine("(Press any key to advance through each stage/artist; Ctrl+C to quit early.)\n");

var summary = new List<(string DisplayName, string SourceId, MatchResult Result)>();

foreach (var artist in artists)
{
    Banner($"ARTIST: {artist.DisplayName}  ({artist.SourceId})");

    PrintObservationAvailability(artist);
    Pause();

    Banner("STAGE: candidate generation, then observations fed ONE AT A TIME (AlbumArtist -> Artist -> Composer)");
    Console.WriteLine("    (each \"-- Observation #N\" line is a single observation entering the sampler; the");
    Console.WriteLine("     evidence lines right after it are what THAT observation produced. NOTE: this whole");
    Console.WriteLine("     stage runs as one uninterrupted live trace below -- pausing INSIDE it, between");
    Console.WriteLine("     candidate generation and each individual observation, would need a pause hook");
    Console.WriteLine("     threaded into SequentialSampler itself; not done here, flagging rather than faking it.\n");

    // Fresh repository per artist -- InMemoryMatchRepository has no per-artist
    // scoping of its own, and reusing one across artists isn't needed for what
    // this smoke test measures.
    var repo = new InMemoryMatchRepository();
    var engine = new ResolutionEngine(plugin, repo, identityCache, scoringConfig, logger);
    var callsBefore = mbClient.TotalApiCalls;
    var result = engine.ResolveOne(artist, context);
    var callsForThisArtist = mbClient.TotalApiCalls - callsBefore;
    Pause();

    Banner("STAGE: artist evidence summary");
    PrintScoreboard(repo, scoringConfig, scorer, mbClient);
    Pause();

    Banner("STAGE: decision");
    PrintDecision(result, scoringConfig);
    Console.WriteLine($"  API load for this artist: {callsForThisArtist} live MBZ call(s) (running total: {mbClient.TotalApiCalls})");
    Pause();

    summary.Add((artist.DisplayName, artist.SourceId, result));
    Console.WriteLine();
}

Banner("SUMMARY -- all artists");
foreach (var (displayName, sourceId, result) in summary)
    Console.WriteLine($"  {displayName,-24} {sourceId,-42} {result.Status,-14} target={result.TargetId} confidence={result.Confidence:F3}");

Console.WriteLine($"\nTotal live MBZ API calls this run: {mbClient.TotalApiCalls}");
foreach (var (callType, count) in mbClient.ApiCallsByType.OrderByDescending(kv => kv.Value))
    Console.WriteLine($"  {callType,-24} {count}");

mbClient.Dispose();
return 0;

// ---------------------------------------------------------------------------

static void Banner(string text)
{
    Console.WriteLine();
    Console.WriteLine(new string('*', 88));
    Console.WriteLine("*  " + text);
    Console.WriteLine(new string('*', 88));
}

static void Pause()
{
    // Skips cleanly when input is redirected/piped (CI, `dotnet run < /dev/null`,
    // output captured to a file) rather than hanging forever waiting for a key
    // that will never come.
    if (Console.IsInputRedirected) return;
    Console.WriteLine("\n[ press any key to continue ]");
    Console.ReadKey(intercept: true);
}

static void PrintObservationAvailability(EmbyArtist artist)
{
    // Deliberately NOT listing every track here -- that's what made the previous
    // version look like a batch feed. What's actually available is just a fact
    // about the source data; which ones get consumed, in what order, one at a
    // time, is the sampler's live decision and is announced by the sampler itself
    // as each one is drawn (see "-- Observation #N" lines in the trace below).
    if (artist.Tracks.Count == 0)
    {
        Console.WriteLine("\n0 observations available for this artist -- static evidence only, no sampling possible.");
        return;
    }
    var counts = new[] { "AlbumArtist", "Artist", "Composer" }
        .Select(role => $"{artist.Tracks.Count(t => t.Role == role)} {role}");
    Console.WriteLine($"\n{artist.Tracks.Count} observation(s) available ({string.Join(", ", counts)}) -- sampled one at a time below, highest tier first.");
}

static void PrintScoreboard(InMemoryMatchRepository repo, ScoringConfig config, SimpleWeightedSumScorer scorer, HttpMusicBrainzApiClient mbClient)
{
    Console.WriteLine($"\n--- ARTIST EVIDENCE SUMMARY  ({repo.Candidates.Count} candidate(s) generated) ---");
    if (repo.Candidates.Count == 0)
    {
        Console.WriteLine("  No candidates were generated for this artist -- nothing to score.");
        return;
    }

    var scored = repo.Candidates
        // BUG FIX 2026-07-17: this was scoring from repo.Evidence unfiltered, silently
        // re-including Contributing=false (opportunistic) evidence in LLR/confidence
        // even though SequentialSampler correctly excludes it from the real decision.
        // Two independent code paths summed evidence differently -- this one must
        // match the sampler's rule exactly: contributing evidence only.
        .Select(c => scorer.Score(c, repo.Evidence.Where(e => e.CandidateId == c.Id && e.Contributing), config))
        .OrderByDescending(s => s.RunningLlr)
        .ToList();

    for (int i = 0; i < scored.Count; i++)
    {
        var s = scored[i];
        var name = mbClient.GetArtistDisplayName(s.Candidate.TargetId);

        // REWRITTEN 2026-07-18 (readability pass, per Nick's feedback): previously
        // this line led with the MBID and the raw MB API query string used to
        // generate the candidate ("query=(artist:\"X\" OR alias:\"X\")"). That's
        // generation-time debugging detail, not evidence, and it crowded out the
        // one thing worth seeing at a glance: the artist's name. "matched via" /
        // "admitted via" wording was considered and dropped -- there is currently
        // only one admission pathway (ArtistStrategy's artist-search stage), so a
        // per-candidate "how was this admitted" line has nothing to distinguish.
        // What IS useful at the candidate level is the actual identity data the
        // candidate carries forward into recording-side matching: its own MBID
        // and any RelationshipMbids (performs-as/is-person links) picked up during
        // the artist-rels fetch -- these are exactly what Tier1 corroboration
        // checks recording credits against, so seeing them here lets you verify
        // by eye whether a later "matched via: relationship MBID" line makes sense.
        Console.WriteLine($"  #{i + 1}  {name}");
        Console.WriteLine($"        artist MBID: {s.Candidate.TargetId}");
        if (s.Candidate.RelationshipMbids.Count > 0)
        {
            Console.WriteLine($"        relationship MBIDs: {string.Join(", ", s.Candidate.RelationshipMbids)}");
        }
        Console.WriteLine($"        LLR={s.RunningLlr:F2}  confidence={s.Confidence:F3}  evidence={s.EvidenceSoFar.Count} contributing record(s)");

        // NOTE ON LLR/confidence SCOPE: these two numbers are per-candidate running
        // totals (see SimpleWeightedSumScorer.Score), summed only over this
        // candidate's Contributing=true evidence records. They are not a property
        // of any single evidence record (a record's own contribution is its
        // EvidenceType's weight in ScoringConfig.EvidenceWeights, printed per type
        // below) and not a single global figure across all candidates -- each
        // candidate ranked here has its own LLR/confidence, independently computed.

        Console.WriteLine("        Scored evidence:");
        var byType = s.EvidenceSoFar.GroupBy(e => e.EvidenceType).OrderByDescending(g => g.Count());
        foreach (var g in byType)
        {
            var llr = config.EvidenceWeights.TryGetValue(g.Key, out var w) ? w.ToString("F2") : "n/a";
            Console.WriteLine($"          {g.Key} x{g.Count()}  (weight {llr} each)");

            // "matched via" is a per-evidence-record fact (EvidenceRecord.MatchedViaAlias /
            // .MatchedViaRelationship), not a per-candidate or per-type one -- different
            // records of the same evidence type can each have matched on a different
            // basis (direct artist MBID vs. a registered alias vs. one of the candidate's
            // RelationshipMbids). Broken out here as a basis-count line under each type
            // rather than folded into the header, so mixed-basis groups stay legible.
            // EXTENSIBILITY NOTE: this reads two booleans (MatchedViaAlias,
            // MatchedViaRelationship) rather than an open set. Fine while there are only
            // two non-default match bases; if a third one is ever added, promote these to
            // a single MatchBasis enum on EvidenceRecord rather than adding a third bool.
            var relationshipHits = g.Count(e => e.MatchedViaRelationship);
            var aliasHits = g.Count(e => e.MatchedViaAlias && !e.MatchedViaRelationship);
            var directHits = g.Count() - relationshipHits - aliasHits;
            var bases = new List<string>();
            if (directHits > 0) bases.Add($"{directHits} via artist MBID");
            if (aliasHits > 0) bases.Add($"{aliasHits} via alias");
            if (relationshipHits > 0) bases.Add($"{relationshipHits} via relationship MBID");
            if (bases.Count > 0)
            {
                Console.WriteLine($"            -- {string.Join(", ", bases)}");
            }
        }

        // Non-scoring evidence for this candidate, shown separately and clearly
        // marked so it can never be mistaken for something that affected LLR/confidence
        // above -- it deliberately did not. See EvidenceRecord.Contributing.
        // Relabeled from "opportunistic" to "Logged, non-scoring" (Nick's feedback):
        // same meaning (Contributing=false), plainer wording.
        var opportunistic = repo.Evidence.Where(e => e.CandidateId == s.Candidate.Id && !e.Contributing).ToList();
        if (opportunistic.Count > 0)
        {
            Console.WriteLine("        Logged, non-scoring:");
            foreach (var g in opportunistic.GroupBy(e => e.EvidenceType).OrderByDescending(g => g.Count()))
            {
                Console.WriteLine($"          {g.Key} x{g.Count()}");
            }
        }
    }
}

static void PrintDecision(MatchResult result, ScoringConfig config)
{
    Console.WriteLine($"\n--- DECISION ---");
    Console.WriteLine($"  status={result.Status}  target={result.TargetId}  confidence={result.Confidence:F3}  margin={result.Margin:F2}");
    Console.WriteLine($"  thresholds: auto_accept >= {config.AutoAcceptThreshold:F2} LLR (with margin >= {config.MinMarginOverRunnerUp:F2}), auto_reject <= {config.AutoRejectThreshold:F2} LLR");
}
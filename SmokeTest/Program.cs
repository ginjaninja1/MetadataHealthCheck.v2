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
        .Select(c => scorer.Score(c, repo.Evidence.Where(e => e.CandidateId == c.Id), config))
        .OrderByDescending(s => s.RunningLlr)
        .ToList();

    for (int i = 0; i < scored.Count; i++)
    {
        var s = scored[i];
        var rank = i == 0 ? "1st" : i == 1 ? "2nd" : $"{i + 1}th";
        var name = mbClient.GetArtistDisplayName(s.Candidate.TargetId);
        Console.WriteLine($"  [{rank}] {s.Candidate.TargetId}  \"{name}\"  strategy={s.Candidate.GenerationStrategy}  query={s.Candidate.GenerationQuery}");
        Console.WriteLine($"        LLR={s.RunningLlr:F2}  confidence={s.Confidence:F3}  evidence={s.EvidenceSoFar.Count} record(s)");

        var byType = s.EvidenceSoFar.GroupBy(e => e.EvidenceType).OrderByDescending(g => g.Count());
        foreach (var g in byType)
        {
            var llr = config.EvidenceWeights.TryGetValue(g.Key, out var w) ? w.ToString("F2") : "n/a";
            Console.WriteLine($"          {g.Key} x{g.Count()}  (weight={llr})");
        }
    }
}

static void PrintDecision(MatchResult result, ScoringConfig config)
{
    Console.WriteLine($"\n--- DECISION ---");
    Console.WriteLine($"  status={result.Status}  target={result.TargetId}  confidence={result.Confidence:F3}  margin={result.Margin:F2}");
    Console.WriteLine($"  thresholds: auto_accept >= {config.AutoAcceptThreshold:F2} LLR (with margin >= {config.MinMarginOverRunnerUp:F2}), auto_reject <= {config.AutoRejectThreshold:F2} LLR");
}
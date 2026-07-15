using MetadataHealthCheck.v2.Core.Engine;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Diagnostics;
using MetadataHealthCheck.v2.Fixtures;
using MetadataHealthCheck.v2.Resolvers.MusicBrainz;
using MetadataHealthCheck.v2.Resolvers.MusicBrainz.Evidence;
using MetadataHealthCheck.v2.Sources.Emby;
using MetadataHealthCheck.v2.Storage;
using SmokeTest;

// REWRITTEN 2026-07-15 (Project Log Directives): this file used to hardcode
// every test artist's track data as inline C# object literals (a Credit(...)
// builder function plus per-case object initializers), interleaved with
// Assert(...) calls checking the exact expected outcome for each one. That's
// exactly the "sample data welded into code" problem flagged this session --
// worse here than FixtureEmbyLibraryReader.cs, since test data and test logic
// were the same code.
//
// Now: real observation data lives in observations.txt (Fixtures/
// TextFileEmbyLibraryReader.cs parses it), and this file just runs every
// artist in it through the real engine and lets the console show what
// happens -- StructuredLogger already prints every Debug/Info line live
// (Log() calls Console.WriteLine directly), so SequentialSampler's own
// per-evidence-record logging is what actually produces the "watch the
// engine mine evidence" trail; nothing extra needed here for that part.
//
// This is now an OBSERVATIONAL smoke test, not an assertion-per-artist one --
// per-artist expected outcomes aren't hardcoded here anymore (that was the
// exact pattern being removed). What DOES still get asserted below is pure
// engine MECHANICS -- the RecordingLookup ladder(s) and MbArtistResult.Aliases
// checks -- since those test a specific class's behavior directly, not
// "what should this real-world artist resolve to", and don't depend on
// observations.txt at all.
int failures = 0;
void Assert(bool cond, string message)
{
    if (cond) { Console.WriteLine($"  PASS: {message}"); }
    else { Console.WriteLine($"  FAIL: {message}"); failures++; }
}

Console.WriteLine("=== MetadataHealthCheck.v2 SmokeTest: observation-driven run ===");
Console.WriteLine("Real MusicBrainz fixture data (Fixtures/FixtureMusicBrainzApiClient.cs),");
Console.WriteLine("real observation data (SmokeTest/observations.txt), real engine. Uses an");
Console.WriteLine("in-memory IMatchRepository, not real SQLite -- see InMemoryMatchRepository.cs");
Console.WriteLine("for why. Every Debug/Info line below is the sampler's own live evidence log,");
Console.WriteLine("not summarized after the fact.\n");

var mbClient = new FixtureMusicBrainzApiClient();
var identityCache = new InMemoryIdentityCache();
var logger = new StructuredLogger();
var scoringConfig = new ScoringConfig();

var plugin = new MusicBrainzArtistResolverPlugin(mbClient, identityCache, scoringConfig);

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

var summary = new List<(string DisplayName, string SourceId, MatchResult Result)>();

foreach (var artist in artists)
{
    // Fresh repository per artist -- InMemoryMatchRepository has no per-artist
    // scoping of its own, and reusing one across artists isn't needed for what
    // this smoke test measures.
    var repo = new InMemoryMatchRepository();
    var engine = new ResolutionEngine(plugin, repo, identityCache, scoringConfig, logger);

    Console.WriteLine($"----- {artist.DisplayName} ({artist.SourceId}) -- {artist.Tracks.Count} track-credit(s) -----");
    var result = engine.ResolveOne(artist, context);
    Console.WriteLine($"==> status={result.Status} target={result.TargetId} confidence={result.Confidence:F3} margin={result.Margin:F2}\n");

    summary.Add((artist.DisplayName, artist.SourceId, result));
}

Console.WriteLine("=== Summary ===");
foreach (var (displayName, sourceId, result) in summary)
    Console.WriteLine($"  {displayName,-24} {sourceId,-42} {result.Status,-14} target={result.TargetId} confidence={result.Confidence:F3}");

Console.WriteLine("\n=== Engine mechanics checks (independent of observations.txt) ===\n");

Console.WriteLine("--- MbArtistResult.Aliases ---");
var sarahVaughanArtistResults = mbClient.SearchArtist("Sarah Vaughan");
var xResult = sarahVaughanArtistResults.First(a => a.Mbid == FixtureMusicBrainzApiClient.MbidX);
Assert(xResult.Aliases.Count == 5, $"Sarah Vaughan (X) carries 5 registered aliases (got {xResult.Aliases.Count})");
Assert(xResult.Aliases.Contains("Sarah Vaughn"), "alias list includes the common single-h misspelling \"Sarah Vaughn\"");
var yResult = sarahVaughanArtistResults.First(a => a.Mbid == FixtureMusicBrainzApiClient.MbidY);
Assert(yResult.Aliases.Count == 0, "the rival same-named artist (Y) carries no aliases of its own");

Console.WriteLine("\n--- RecordingLookup three-rung fallback ladder ---");
var recordingLookup = new RecordingLookup(mbClient);

var autumnLeavesTrack = new EmbyTrackCredit
{
    TrackId = "track-autumn-leaves-ladder-check",
    TrackName = "Autumn Leaves",
    AlbumName = "Crazy and Mixed Up",
    AlbumId = "album-crazy-and-mixed-up",
    Role = "AlbumArtist",
};
var rung1Lookup = recordingLookup.Lookup(FixtureMusicBrainzApiClient.MbidX, autumnLeavesTrack, artistName: "Sarah Vaughan");
Assert(rung1Lookup.RungReached == RecordingLookupRung.TrackArtistAlbum, $"Autumn Leaves resolves on rung 1 (track+artist+album), got {rung1Lookup.RungReached}");
Assert(rung1Lookup.Recording?.RecordingId == "rec-autumn-leaves-X", "rung 1 lookup returns the correct recording id");

var ladderFallbackTrack = new EmbyTrackCredit
{
    TrackId = "track-ladder-fallback-check",
    TrackName = "Ladder Fallback Case",
    AlbumName = "A Different Listed Album", // deliberately wrong -- rungs 1/2 must both miss
    AlbumId = "album-ladder-fallback",
    Role = "AlbumArtist",
};
var rung3Lookup = recordingLookup.Lookup(FixtureMusicBrainzApiClient.MbidX, ladderFallbackTrack, artistName: "Sarah Vaughan");
Assert(rung3Lookup.RungReached == RecordingLookupRung.TrackOnly, $"purpose-built case only resolves on rung 3 (track alone), got {rung3Lookup.RungReached}");
Assert(rung3Lookup.Recording?.RecordingId == "rec-ladder-fallback", "rung 3 lookup returns the correct recording id");

var noHitLookup = recordingLookup.Lookup("mbid-nobody", ladderFallbackTrack, artistName: null);
Assert(noHitLookup.RungReached == RecordingLookupRung.NotFound && noHitLookup.Recording == null, "a candidate with no matching recording at any rung correctly returns NotFound");

var cachedLookup = recordingLookup.Lookup(FixtureMusicBrainzApiClient.MbidX, autumnLeavesTrack, artistName: "Sarah Vaughan");
Assert(ReferenceEquals(rung1Lookup, cachedLookup), "repeat lookup for the same (candidate, track) pair returns the memoized instance, not a recomputed one");

Console.WriteLine("\n--- RecordingLookup composer-tier relationship-scan (new, 2026-07-15) ---");
var borrowedTimeTrack = new EmbyTrackCredit
{
    TrackId = "track-borrowed-time-check",
    TrackName = "Borrowed Time",
    AlbumName = "One Cell in the Sea",
    AlbumId = "album-one-cell-in-the-sea",
    Role = "Composer",
};
var composerLookup = recordingLookup.LookupComposerTier(FixtureMusicBrainzApiClient.MbidGusBlack, borrowedTimeTrack, new[] { "A Fine Frenzy" });
Assert(composerLookup.Recording?.RecordingId == "rec-borrowed-time", $"Gus Black's composer credit on \"Borrowed Time\" is now found via relationship-scan (got recording={composerLookup.Recording?.RecordingId ?? "null"})");
Assert(composerLookup.RungReached == RecordingLookupRung.ComposerBorrowedNameTrackAlbum, $"found via the borrowed-name rung (A Fine Frenzy's own credit narrowed the search), got {composerLookup.RungReached}");

Console.WriteLine($"\n=== {(failures == 0 ? "ALL MECHANICS CHECKS PASS" : failures + " MECHANICS CHECK FAILURE(S)")} ===");
return failures == 0 ? 0 : 1;
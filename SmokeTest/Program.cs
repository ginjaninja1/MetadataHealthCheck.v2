using MetadataHealthCheck.v2.Core.Engine;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Diagnostics;
using MetadataHealthCheck.v2.Fixtures;
using MetadataHealthCheck.v2.Resolvers.MusicBrainz;
using MetadataHealthCheck.v2.Sources.Emby;
using MetadataHealthCheck.v2.Storage;
using MetadataHealthCheck.v2.Storage.Sqlite;

int failures = 0;
void Assert(bool cond, string message)
{
    if (cond) { Console.WriteLine($"  PASS: {message}"); }
    else { Console.WriteLine($"  FAIL: {message}"); failures++; }
}

Console.WriteLine("=== Phase 1 End-to-End Skeleton Test ===");

var dbPath = Path.Combine(Path.GetTempPath(), $"mhc-v2-smoketest-{Guid.NewGuid():N}.db");
Console.WriteLine($"Using SQLite db: {dbPath}");

var mbClient = new FixtureMusicBrainzApiClient();
var identityCache = new InMemoryIdentityCache();
var repo = new MatchRepository(dbPath);
var logger = new StructuredLogger();
var scoringConfig = new ScoringConfig();

var plugin = new MusicBrainzArtistResolverPlugin(mbClient, identityCache);
var engine = new ResolutionEngine(plugin, repo, identityCache, scoringConfig, logger);

var emby = new FixtureEmbyLibraryReader();
var provider = new EmbyArtistProvider(emby);
var context = new ResolutionContext();

var artists = provider.GetAll(context).ToList();
Assert(artists.Count == 1, $"exactly one fixture artist read (got {artists.Count})");

var artist = artists[0];
var result = engine.ResolveOne(artist, context);

Console.WriteLine($"\nDecision: target={result.TargetId} status={result.Status} confidence={result.Confidence:F3} margin={result.Margin:F2}");

Assert(result.TargetId == FixtureMusicBrainzApiClient.MbidX, "correct candidate (X) selected as top match");
Assert(result.Status is "auto_accept" or "needs_review", "decision status is auto_accept or needs_review (not auto_reject) for the clearly-better candidate");

// Re-run should hit the identity cache if the first run auto-accepted.
if (result.Status == "auto_accept")
{
    var second = engine.ResolveOne(artist, context);
    Assert(second.TargetId == result.TargetId, "identity cache reuse returns same target on second resolution");
    Assert(logger.Lines.Any(l => l.Contains("Identity cache hit")), "identity cache hit was logged");
}

// Verify SQLite persistence actually happened (not just in-memory).
var persisted = repo.GetExisting(artist.SourceSystem, artist.SourceId, "MusicBrainz");
Assert(persisted != null, "match result persisted to SQLite and re-readable");
Assert(persisted != null && persisted.TargetId == result.TargetId, "persisted target id matches decision");

Console.WriteLine("\n--- Structured log ---");
foreach (var line in logger.Lines) Console.WriteLine(line);

// Edge case: an artist with no matching MusicBrainz data at all should not
// crash and should land on needs_review (empty candidate list), not throw.
var unknownArtist = new EmbyArtist
{
    SourceId = "emby-artist-unknown",
    DisplayName = "Totally Unknown Artist XYZ",
    Tracks = new List<EmbyTrackCredit>
    {
        new EmbyTrackCredit { TrackId = "t1", TrackName = "Nonexistent Song", AlbumName = "Nonexistent Album", AlbumId = "a1", Role = "AlbumArtist" }
    }
};
var unknownResult = engine.ResolveOne(unknownArtist, context);
Assert(unknownResult.Status == "needs_review", $"unresolvable artist lands on needs_review, not a crash or false accept (got {unknownResult.Status})");

Console.WriteLine($"\n=== {(failures == 0 ? "ALL PASS" : failures + " FAILURE(S)")} ===");
File.Delete(dbPath);
if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal");
if (File.Exists(dbPath + "-shm")) File.Delete(dbPath + "-shm");

return failures == 0 ? 0 : 1;

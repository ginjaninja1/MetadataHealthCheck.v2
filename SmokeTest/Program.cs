using MetadataHealthCheck.v2.Core.Engine;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Diagnostics;
using MetadataHealthCheck.v2.Fixtures;
using MetadataHealthCheck.v2.Resolvers.MusicBrainz;
using MetadataHealthCheck.v2.Sources.Emby;
using MetadataHealthCheck.v2.Storage;
using SmokeTest;

int failures = 0;
void Assert(bool cond, string message)
{
    if (cond) { Console.WriteLine($"  PASS: {message}"); }
    else { Console.WriteLine($"  FAIL: {message}"); failures++; }
}

Console.WriteLine("=== Phase 1 End-to-End Skeleton Test (in-memory repository) ===");
Console.WriteLine("NOTE: uses an in-memory IMatchRepository, not real SQLite - see");
Console.WriteLine("InMemoryMatchRepository.cs for why. This verifies engine logic");
Console.WriteLine("(candidate generation, scoring, decisions, identity cache); real");
Console.WriteLine("SQLite persistence should be verified separately inside an actual");
Console.WriteLine("Emby host.\n");

var mbClient = new FixtureMusicBrainzApiClient();
var identityCache = new InMemoryIdentityCache();
var logger = new StructuredLogger();
var repo = new InMemoryMatchRepository();
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

if (result.Status == "auto_accept")
{
    var second = engine.ResolveOne(artist, context);
    Assert(second.TargetId == result.TargetId, "identity cache reuse returns same target on second resolution");
    Assert(logger.Lines.Any(l => l.Contains("Identity cache hit")), "identity cache hit was logged");
}

var persisted = repo.GetExisting(artist.SourceSystem, artist.SourceId, "MusicBrainz");
Assert(persisted != null, "match result saved and re-readable via GetExisting");
Assert(persisted != null && persisted.TargetId == result.TargetId, "retrieved target id matches decision");

Console.WriteLine("\n--- Structured log ---");
foreach (var line in logger.Lines) Console.WriteLine(line);

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

Console.WriteLine("\n=== Sequential Sampler: multi-track adaptive stop ===");
Console.WriteLine("Two AlbumArtist-tier tracks: the first carries no work-relationship");
Console.WriteLine("evidence at all, the second does. A correct sampler draws the first,");
Console.WriteLine("stays at needs_review, draws the second, then stops -- it must not");
Console.WriteLine("false-accept after track 1, and there's no track 3 to fall back on if");
Console.WriteLine("it mistakenly kept going.\n");

var multiTrackArtist = new EmbyArtist
{
    SourceId = "emby-artist-sarah-vaughan-multitrack",
    DisplayName = "Sarah Vaughan",
    Tracks = new List<EmbyTrackCredit>
    {
        new EmbyTrackCredit { TrackId = "t-different-song", TrackName = "A Different Song Entirely", AlbumName = "Some Earlier Album", AlbumId = "album-some-earlier-album", Role = "AlbumArtist" },
        new EmbyTrackCredit { TrackId = "t-autumn-leaves", TrackName = "Autumn Leaves", AlbumName = "Crazy and Mixed Up", AlbumId = "album-crazy-and-mixed-up", Role = "AlbumArtist" },
    }
};
var samplerLoggerStart = logger.Lines.Count;
var multiResult = engine.ResolveOne(multiTrackArtist, context);

Assert(multiResult.Status == "auto_accept", $"multi-track artist resolves to auto_accept (got {multiResult.Status})");
Assert(multiResult.TargetId == FixtureMusicBrainzApiClient.MbidX, "multi-track artist resolves to the correct candidate (X)");
Assert(
    logger.Lines.Skip(samplerLoggerStart).Any(l => l.Contains("resolved after 2 observation(s) in bucket AlbumArtist")),
    "sampler drew exactly 2 observations before resolving -- the first track alone wasn't enough, the second was, and there was no unnecessary third draw");
Assert(
    !logger.Lines.Skip(samplerLoggerStart).Any(l => l.Contains("resolved after 1 observation(s)")),
    "sampler did not stop after track 1 alone (which carries no work-relationship evidence) -- would indicate a false-accept bug");

Console.WriteLine("\n=== Sequential Sampler: bucket escalation ===");
Console.WriteLine("AlbumArtist tier has only 2 tracks, both dead (no evidence) --");
Console.WriteLine("bucket runs out naturally, not via the ceiling. One Artist-tier");
Console.WriteLine("track follows with real evidence. A correct sampler escalates and");
Console.WriteLine("resolves there, rather than giving up when AlbumArtist runs dry.\n");

var escalationArtist = new EmbyArtist
{
    SourceId = "emby-artist-sarah-vaughan-escalation",
    DisplayName = "Sarah Vaughan",
    Tracks = new List<EmbyTrackCredit>
    {
        new EmbyTrackCredit { TrackId = "e-dead-1", TrackName = "Silent Track One", AlbumName = "Quiet Album A", AlbumId = "album-quiet-a", Role = "AlbumArtist" },
        new EmbyTrackCredit { TrackId = "e-dead-2", TrackName = "Silent Track Two", AlbumName = "Quiet Album B", AlbumId = "album-quiet-b", Role = "AlbumArtist" },
        new EmbyTrackCredit { TrackId = "e-autumn-leaves-artist-tier", TrackName = "Autumn Leaves", AlbumName = "Crazy and Mixed Up", AlbumId = "album-crazy-and-mixed-up", Role = "Artist" },
    }
};
var escalationLoggerStart = logger.Lines.Count;
var escalationResult = engine.ResolveOne(escalationArtist, context);

Assert(escalationResult.Status == "auto_accept", $"escalation artist resolves to auto_accept (got {escalationResult.Status})");
Assert(
    logger.Lines.Skip(escalationLoggerStart).Any(l => l.Contains("resolved after 1 observation(s) in bucket Artist")),
    "sampler escalated past the exhausted AlbumArtist bucket and resolved in Artist after exactly 1 observation there");
Assert(
    !logger.Lines.Skip(escalationLoggerStart).Any(l => l.Contains("in bucket AlbumArtist:")),
    "sampler did not (incorrectly) resolve within AlbumArtist -- both of its tracks were dead");

Console.WriteLine("\n=== Sequential Sampler: BucketCeiling enforcement ===");
Console.WriteLine("3 dead AlbumArtist tracks fill the ceiling (default 3), then a 4th");
Console.WriteLine("AlbumArtist track with real evidence follows. A correct sampler must");
Console.WriteLine("stop at the ceiling and never look at track 4 -- landing on");
Console.WriteLine("needs_review despite better evidence existing one track later.\n");

var ceilingArtist = new EmbyArtist
{
    SourceId = "emby-artist-sarah-vaughan-ceiling",
    DisplayName = "Sarah Vaughan",
    Tracks = new List<EmbyTrackCredit>
    {
        new EmbyTrackCredit { TrackId = "c-dead-1", TrackName = "Ceiling Filler One", AlbumName = "Filler Album 1", AlbumId = "album-filler-1", Role = "AlbumArtist" },
        new EmbyTrackCredit { TrackId = "c-dead-2", TrackName = "Ceiling Filler Two", AlbumName = "Filler Album 2", AlbumId = "album-filler-2", Role = "AlbumArtist" },
        new EmbyTrackCredit { TrackId = "c-dead-3", TrackName = "Ceiling Filler Three", AlbumName = "Filler Album 3", AlbumId = "album-filler-3", Role = "AlbumArtist" },
        new EmbyTrackCredit { TrackId = "c-autumn-leaves-blocked", TrackName = "Autumn Leaves", AlbumName = "Crazy and Mixed Up", AlbumId = "album-crazy-and-mixed-up", Role = "AlbumArtist" },
    }
};
var ceilingResult = engine.ResolveOne(ceilingArtist, context);
Assert(
    ceilingResult.Status == "needs_review",
    $"artist with real evidence sitting just past the AlbumArtist ceiling still lands on needs_review, not auto_accept (got {ceilingResult.Status}) -- ceiling was not enforced if this fails");

Console.WriteLine("\n=== Sequential Sampler: full exhaustion, no crash ===");
Console.WriteLine("Dead tracks across all three buckets. Nothing resolves anywhere --");
Console.WriteLine("must terminate cleanly at needs_review, not hang or throw.\n");

var exhaustionArtist = new EmbyArtist
{
    SourceId = "emby-artist-sarah-vaughan-exhaustion",
    DisplayName = "Sarah Vaughan",
    Tracks = new List<EmbyTrackCredit>
    {
        new EmbyTrackCredit { TrackId = "x-albumartist-1", TrackName = "Nothing Here AA1", AlbumName = "Empty Album AA1", AlbumId = "album-empty-aa1", Role = "AlbumArtist" },
        new EmbyTrackCredit { TrackId = "x-albumartist-2", TrackName = "Nothing Here AA2", AlbumName = "Empty Album AA2", AlbumId = "album-empty-aa2", Role = "AlbumArtist" },
        new EmbyTrackCredit { TrackId = "x-artist-1", TrackName = "Nothing Here A1", AlbumName = "Empty Album A1", AlbumId = "album-empty-a1", Role = "Artist" },
        new EmbyTrackCredit { TrackId = "x-artist-2", TrackName = "Nothing Here A2", AlbumName = "Empty Album A2", AlbumId = "album-empty-a2", Role = "Artist" },
        new EmbyTrackCredit { TrackId = "x-composer-1", TrackName = "Nothing Here C1", AlbumName = "Empty Album C1", AlbumId = "album-empty-c1", Role = "Composer" },
        new EmbyTrackCredit { TrackId = "x-composer-2", TrackName = "Nothing Here C2", AlbumName = "Empty Album C2", AlbumId = "album-empty-c2", Role = "Composer" },
    }
};
var exhaustionLoggerStart = logger.Lines.Count;
var exhaustionResult = engine.ResolveOne(exhaustionArtist, context);
Assert(exhaustionResult.Status == "needs_review", $"fully exhausted artist lands cleanly on needs_review (got {exhaustionResult.Status})");
Assert(
    logger.Lines.Skip(exhaustionLoggerStart).Any(l => l.Contains("exhausted all bucket budgets")),
    "sampler logged that it exhausted every bucket's budget, confirming it walked through AlbumArtist, Artist, and Composer rather than stopping short");

Console.WriteLine("\n=== Real-world case: Gus Black (composer-only, single track) ===");
Console.WriteLine("Real composer credit on 'Borrowed Time' (A Fine Frenzy's 'One Cell in");
Console.WriteLine("the Sea'), externally corroborated. But candidate generation is entirely");
Console.WriteLine("performer-credit driven -- the only candidate this track can produce is");
Console.WriteLine("A Fine Frenzy (the real performer), not Gus Black. Expected: that spurious");
Console.WriteLine("candidate gets generated, then correctly rejected on name-mismatch --");
Console.WriteLine("landing on needs_review. Never falsely accepts the wrong artist, but also");
Console.WriteLine("never finds the right one. Known, expected gap (composer-only candidate");
Console.WriteLine("generation is Phase 3 territory), not a bug in this fixture.\n");

var gusBlackArtist = new EmbyArtist
{
    SourceId = "emby-artist-gus-black",
    DisplayName = "Gus Black",
    Tracks = new List<EmbyTrackCredit>
    {
        new EmbyTrackCredit { TrackId = "gb-borrowed-time", TrackName = "Borrowed Time", AlbumName = "One Cell in the Sea", AlbumId = "album-one-cell-in-the-sea", Role = "Composer" }
    }
};
var gusBlackLoggerStart = logger.Lines.Count;
var gusBlackResult = engine.ResolveOne(gusBlackArtist, context);

Assert(gusBlackResult.Status == "needs_review", $"Gus Black lands on needs_review -- the spurious A Fine Frenzy candidate is correctly not accepted (got {gusBlackResult.Status})");
Assert(gusBlackResult.TargetId != FixtureMusicBrainzApiClient.MbidGusBlack, "Gus Black's own MBID is never even considered as a candidate today -- composer-only candidate generation isn't built yet");
Assert(
    logger.Lines.Skip(gusBlackLoggerStart).Any(l => l.Contains("NameSimilarity.Poor")),
    "the spurious A Fine Frenzy candidate is correctly flagged as a poor name match against 'Gus Black' -- that mismatch is what prevents a false accept");

Console.WriteLine("\n=== Real-world case: Queen on a non-canonical compilation album ===");
Console.WriteLine("Queen's credit here is Artist-tier (performer), not AlbumArtist --");
Console.WriteLine("'Various Artists' holds that role on this radio compilation. No");
Console.WriteLine("composer/writer-type collector applies to a performer-only band credit,");
Console.WriteLine("so this is expected to land on needs_review -- demonstrating the real");
Console.WriteLine("gap (no recording/performer-tier evidence collector yet), not a problem");
Console.WriteLine("with the odd album metadata itself. Unlike Gus Black, the RIGHT");
Console.WriteLine("candidate does get generated here -- it's just under-evidenced.\n");

var queenArtist = new EmbyArtist
{
    SourceId = "emby-artist-queen",
    DisplayName = "Queen",
    Tracks = new List<EmbyTrackCredit>
    {
        new EmbyTrackCredit { TrackId = "q-bohemian-rhapsody", TrackName = "Bohemian Rhapsody", AlbumName = "Top 2000 (Radio 2)", AlbumId = "album-top-2000-radio-2", Role = "Artist" }
    }
};
var queenResult = engine.ResolveOne(queenArtist, context);
Assert(queenResult.Status == "needs_review", $"Queen (Artist-tier performer credit, no composer-type match available) lands on needs_review, exposing the missing performer-tier evidence collector (got {queenResult.Status})");
Assert(queenResult.TargetId == FixtureMusicBrainzApiClient.MbidQueen, "unlike Gus Black/Del Serino, Queen's own MBID IS correctly identified as the candidate -- just not confidently enough to auto-accept");

Console.WriteLine("\n=== Real-world case: Florence + the Machine naming variants ===");
Console.WriteLine("Three real-world taggings of the same artist. MusicBrainz's own sort-name");
Console.WriteLine("for this artist is literally 'Florence and the Machine', while 'Florence +");
Console.WriteLine("the Machine' is the stylized display form -- so 'and'/'&' aren't tagging");
Console.WriteLine("errors, they're how MB itself refers to this artist. Expected similarity");
Console.WriteLine("buckets computed by hand against the real Levenshtein algorithm before");
Console.WriteLine("writing these assertions, not guessed.\n");

var florenceVariants = new (string sourceId, string displayName, string expectedBucket)[]
{
    ("emby-artist-florence-and", "Florence and the machine", "NameSimilarity.Close"),
    ("emby-artist-florence-amp", "Florence & The Machine", "NameSimilarity.NearExact"),
    ("emby-artist-florence-plus-lower", "Florence + The machine", "NameSimilarity.NearExact"),
};

foreach (var (sourceId, displayName, expectedBucket) in florenceVariants)
{
    var florenceArtist = new EmbyArtist
    {
        SourceId = sourceId,
        DisplayName = displayName,
        Tracks = new List<EmbyTrackCredit>
        {
            new EmbyTrackCredit { TrackId = sourceId + "-track", TrackName = "Dog Days Are Over", AlbumName = "Lungs", AlbumId = "album-lungs", Role = "AlbumArtist" }
        }
    };
    var startLine = logger.Lines.Count;
    var florenceResult = engine.ResolveOne(florenceArtist, context);

    Assert(
        logger.Lines.Skip(startLine).Any(l => l.Contains(expectedBucket)),
        $"\"{displayName}\" vs \"Florence + the Machine\" classified as {expectedBucket}, as computed");
    Assert(
        florenceResult.TargetId == FixtureMusicBrainzApiClient.MbidFlorence,
        $"\"{displayName}\" correctly generates and identifies the Florence + the Machine candidate (right artist, just not enough evidence to auto-accept on name alone)");
    Assert(
        florenceResult.Status == "needs_review",
        $"\"{displayName}\" lands on needs_review (the track carries no work-relationship evidence by design -- static name-similarity alone, even at its best, is below the auto-accept threshold; got {florenceResult.Status})");
}

Console.WriteLine("\n=== Real-world case: Del Serino (composer alias, same gap as Gus Black) ===");
Console.WriteLine("Real composer credit (with Roy Alfred) on Adele's '19' cover of \"That's");
Console.WriteLine("It, I Quit, I'm Moving On\", externally corroborated. Same structural gap");
Console.WriteLine("as Gus Black: the only candidate this track can produce is Adele (the real");
Console.WriteLine("performer). This does NOT yet prove anything about alias resolution");
Console.WriteLine("specifically -- that layer isn't even reachable until composer-candidate-");
Console.WriteLine("generation exists (Phase 3). Kept in the pot because it should start to");
Console.WriteLine("differentiate from Gus Black once that's built.\n");

var delSerinoArtist = new EmbyArtist
{
    SourceId = "emby-artist-del-serino",
    DisplayName = "Del Serino",
    Tracks = new List<EmbyTrackCredit>
    {
        new EmbyTrackCredit { TrackId = "ds-thats-it-i-quit", TrackName = "That's It, I Quit, I'm Moving On", AlbumName = "19", AlbumId = "album-19", Role = "Composer" }
    }
};
var delSerinoResult = engine.ResolveOne(delSerinoArtist, context);
Assert(delSerinoResult.Status == "needs_review", $"Del Serino lands on needs_review, same structural gap as Gus Black (got {delSerinoResult.Status})");
Assert(delSerinoResult.TargetId != FixtureMusicBrainzApiClient.MbidDelSerino, "Del Serino's own MBID is never considered as a candidate today, for the same reason as Gus Black");

Console.WriteLine($"\n=== {(failures == 0 ? "ALL PASS" : failures + " FAILURE(S)")} ===");

return failures == 0 ? 0 : 1;
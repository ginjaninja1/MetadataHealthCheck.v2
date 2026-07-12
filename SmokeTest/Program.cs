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

// Populates the widened EmbyTrackCredit fields (AlbumArtists/Artists/Composers/
// Duration, added 2026-07-12) uniformly for every synthetic test track below --
// these are placeholder EmbyIds/durations, not verified real data, unlike
// FixtureEmbyLibraryReader.cs's Sarah Vaughan/Autumn Leaves case (which uses a
// real confirmed duration, 335240ms, from the actual MusicBrainz recording).
// Populating placeholders here still keeps every test track internally
// consistent with the real per-tier credit shape, rather than leaving every
// case but one with empty lists.
static EmbyTrackCredit Credit(string trackId, string trackName, string albumName, string albumId, string role, string artistDisplayName, string artistEmbyId, TimeSpan? duration = null)
{
    var credit = new EmbyTrackCredit
    {
        TrackId = trackId,
        TrackName = trackName,
        AlbumName = albumName,
        AlbumId = albumId,
        Role = role,
        Duration = duration ?? TimeSpan.FromMinutes(3.5),
    };
    var self = new List<EmbyCreditedName> { new EmbyCreditedName { Name = artistDisplayName, EmbyId = artistEmbyId } };
    switch (role)
    {
        case "AlbumArtist": credit.AlbumArtists = self; break;
        case "Artist": credit.Artists = self; break;
        case "Composer": credit.Composers = self; break;
    }
    return credit;
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

// §18 worked-example reproduction (§21 phase 2's stated goal): this fixture case
// (FixtureEmbyLibraryReader.cs) IS §18's exact setup -- single AlbumArtist-tier
// track "Autumn Leaves"/"Crazy and Mixed Up", two candidates X (correct) and Y.
// Structural behavior matches §18: auto-accept, correct candidate, single
// observation, comfortably over MinMarginOverRunnerUp. The absolute LLR numbers
// do NOT match §18's own illustrative math, for two found-during-implementation
// reasons, both worth a second look rather than silently accepted:
//   1. §18's prose never mentions a WorkRelationship credit, but this fixture's
//      "Autumn Leaves" genuinely carries one for X (+2.5) -- so X's real total
//      (7.5) comes out higher than §18's stated 5.0.
//   2. §18 assumes Y gets a -2.0 penalty for "no track/album match" -- but no
//      such negative-evidence type exists anywhere in §6.1's catalog. This
//      fixture's Y-recording is legitimately attributed to Y's own MBID, just
//      weakly (Tier 3, neither title field corroborates), giving Y a small
//      *positive* +0.5 rather than a negative figure. §18's illustrative -2.0
//      doesn't correspond to any evidence type that was actually built.
// Margin (7.0) happens to match §18's stated 7.0 anyway -- both totals moved up
// by a similar amount, which is a coincidence of this fixture's specific numbers,
// not a sign the underlying math matches §18's intent point-for-point.
Assert(Math.Abs(result.Margin - 7.0) < 0.01, $"margin over runner-up matches §18's stated 7.0 nats (got {result.Margin:F2}) -- coincidental given the two divergences noted above, not exact reproduction of §18's own per-candidate math");
Assert(result.Status == "auto_accept", "§18's setup resolves to auto_accept, as the worked example describes");

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
        Credit("t1", "Nonexistent Song", "Nonexistent Album", "a1", "AlbumArtist", "Totally Unknown Artist XYZ", "emby-artist-unknown")
    }
};
var unknownResult = engine.ResolveOne(unknownArtist, context);
Assert(unknownResult.Status == "needs_review", $"unresolvable artist lands on needs_review, not a crash or false accept (got {unknownResult.Status})");

Console.WriteLine("\n=== Sequential Sampler: multi-track adaptive stop ===");
Console.WriteLine("Two AlbumArtist-tier tracks: the first corroborates only weakly (Tier 3 --");
Console.WriteLine("a recording exists under X's own MBID, but neither track nor album title");
Console.WriteLine("lines up), the second corroborates fully (Tier 1). A correct sampler draws");
Console.WriteLine("the first, stays at needs_review (static baseline + Tier 3 alone falls");
Console.WriteLine("short of the accept threshold), draws the second, then stops.\n");
Console.WriteLine("NOTE: prior to CorroborationTierEvidenceCollector existing, this test used");
Console.WriteLine("a track with literally no recording match at all as the 'weak' first draw.");
Console.WriteLine("That no longer forces two observations: the static baseline alone (name");
Console.WriteLine("similarity + the AlbumMatch precursor, 3.0 nats) plus almost any real");
Console.WriteLine("corroboration now crosses the 4.0 threshold on the first useful observation.");
Console.WriteLine("Updated to a genuinely weak (Tier 3) first observation to keep testing what");
Console.WriteLine("this test is actually for.\n");

var multiTrackArtist = new EmbyArtist
{
    SourceId = "emby-artist-sarah-vaughan-multitrack",
    DisplayName = "Sarah Vaughan",
    Tracks = new List<EmbyTrackCredit>
    {
        Credit("t-loosely-related", "A Loosely Related Track", "Some Earlier Album", "album-some-earlier-album", "AlbumArtist", "Sarah Vaughan", "emby-artist-sarah-vaughan-multitrack"),
        Credit("t-autumn-leaves", "Autumn Leaves", "Crazy and Mixed Up", "album-crazy-and-mixed-up", "AlbumArtist", "Sarah Vaughan", "emby-artist-sarah-vaughan-multitrack"),
    }
};
var samplerLoggerStart = logger.Lines.Count;
var multiResult = engine.ResolveOne(multiTrackArtist, context);

Assert(multiResult.Status == "auto_accept", $"multi-track artist resolves to auto_accept (got {multiResult.Status})");
Assert(multiResult.TargetId == FixtureMusicBrainzApiClient.MbidX, "multi-track artist resolves to the correct candidate (X)");
Assert(
    logger.Lines.Skip(samplerLoggerStart).Any(l => l.Contains("resolved after 2 observation(s) in bucket AlbumArtist")),
    "sampler drew exactly 2 observations before resolving -- the first track's weak (Tier 3) evidence alone wasn't enough, the second (Tier 1) was, and there was no unnecessary third draw");
Assert(
    !logger.Lines.Skip(samplerLoggerStart).Any(l => l.Contains("resolved after 1 observation(s)")),
    "sampler did not stop after track 1 alone (Tier 3 corroboration only) -- would indicate a false-accept bug");

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
        Credit("e-dead-1", "Silent Track One", "Quiet Album A", "album-quiet-a", "AlbumArtist", "Sarah Vaughan", "emby-artist-sarah-vaughan-escalation"),
        Credit("e-dead-2", "Silent Track Two", "Quiet Album B", "album-quiet-b", "AlbumArtist", "Sarah Vaughan", "emby-artist-sarah-vaughan-escalation"),
        Credit("e-autumn-leaves-artist-tier", "Autumn Leaves", "Crazy and Mixed Up", "album-crazy-and-mixed-up", "Artist", "Sarah Vaughan", "emby-artist-sarah-vaughan-escalation"),
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
        Credit("c-dead-1", "Ceiling Filler One", "Filler Album 1", "album-filler-1", "AlbumArtist", "Sarah Vaughan", "emby-artist-sarah-vaughan-ceiling"),
        Credit("c-dead-2", "Ceiling Filler Two", "Filler Album 2", "album-filler-2", "AlbumArtist", "Sarah Vaughan", "emby-artist-sarah-vaughan-ceiling"),
        Credit("c-dead-3", "Ceiling Filler Three", "Filler Album 3", "album-filler-3", "AlbumArtist", "Sarah Vaughan", "emby-artist-sarah-vaughan-ceiling"),
        Credit("c-autumn-leaves-blocked", "Autumn Leaves", "Crazy and Mixed Up", "album-crazy-and-mixed-up", "AlbumArtist", "Sarah Vaughan", "emby-artist-sarah-vaughan-ceiling"),
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
        Credit("x-albumartist-1", "Nothing Here AA1", "Empty Album AA1", "album-empty-aa1", "AlbumArtist", "Sarah Vaughan", "emby-artist-sarah-vaughan-exhaustion"),
        Credit("x-albumartist-2", "Nothing Here AA2", "Empty Album AA2", "album-empty-aa2", "AlbumArtist", "Sarah Vaughan", "emby-artist-sarah-vaughan-exhaustion"),
        Credit("x-artist-1", "Nothing Here A1", "Empty Album A1", "album-empty-a1", "Artist", "Sarah Vaughan", "emby-artist-sarah-vaughan-exhaustion"),
        Credit("x-artist-2", "Nothing Here A2", "Empty Album A2", "album-empty-a2", "Artist", "Sarah Vaughan", "emby-artist-sarah-vaughan-exhaustion"),
        Credit("x-composer-1", "Nothing Here C1", "Empty Album C1", "album-empty-c1", "Composer", "Sarah Vaughan", "emby-artist-sarah-vaughan-exhaustion"),
        Credit("x-composer-2", "Nothing Here C2", "Empty Album C2", "album-empty-c2", "Composer", "Sarah Vaughan", "emby-artist-sarah-vaughan-exhaustion"),
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
        Credit("gb-borrowed-time", "Borrowed Time", "One Cell in the Sea", "album-one-cell-in-the-sea", "Composer", "Gus Black", "emby-artist-gus-black")
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
Console.WriteLine("'Various Artists' holds that role on this radio compilation. This is the");
Console.WriteLine("case that originally motivated RecordingRelationshipEvidenceCollector: no");
Console.WriteLine("composer/writer-type collector applies to a performer-only band credit, so");
Console.WriteLine("this used to land on needs_review, under-evidenced. With a real recording-");
Console.WriteLine("level producer credit (Queen co-produced 'Bohemian Rhapsody' with Roy Thomas");
Console.WriteLine("Baker, externally corroborated) plus Tier 2 corroboration now both firing,");
Console.WriteLine("it resolves correctly.\n");

var queenArtist = new EmbyArtist
{
    SourceId = "emby-artist-queen",
    DisplayName = "Queen",
    Tracks = new List<EmbyTrackCredit>
    {
        Credit("q-bohemian-rhapsody", "Bohemian Rhapsody", "Top 2000 (Radio 2)", "album-top-2000-radio-2", "Artist", "Queen", "emby-artist-queen")
    }
};
var queenResult = engine.ResolveOne(queenArtist, context);
Assert(queenResult.Status == "auto_accept", $"Queen (Artist-tier performer credit) now resolves to auto_accept with RecordingRelationship + CorroborationTier evidence, closing the gap this fixture case was built to expose (got {queenResult.Status})");
Assert(queenResult.TargetId == FixtureMusicBrainzApiClient.MbidQueen, "Queen's own MBID is correctly identified and accepted as the candidate");

Console.WriteLine("\n=== Real-world case: Florence + the Machine naming variants ===");
Console.WriteLine("Three real-world taggings of the same artist. MusicBrainz's own sort-name");
Console.WriteLine("for this artist is literally 'Florence and the Machine', while 'Florence +");
Console.WriteLine("the Machine' is the stylized display form -- so 'and'/'&' aren't tagging");
Console.WriteLine("errors, they're how MB itself refers to this artist. Expected similarity");
Console.WriteLine("buckets computed by hand against the real Levenshtein algorithm before");
Console.WriteLine("writing these assertions, not guessed. The name-similarity bucket is still");
Console.WriteLine("the point of this test; the overall status now also auto-accepts, since");
Console.WriteLine("'Dog Days Are Over'/'Lungs' is a genuine Tier 1 full-triple match --");
Console.WriteLine("CorroborationTier didn't exist when this test was first written.\n");

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
            Credit(sourceId + "-track", "Dog Days Are Over", "Lungs", "album-lungs", "AlbumArtist", displayName, sourceId)
        }
    };
    var startLine = logger.Lines.Count;
    var florenceResult = engine.ResolveOne(florenceArtist, context);

    Assert(
        logger.Lines.Skip(startLine).Any(l => l.Contains(expectedBucket)),
        $"\"{displayName}\" vs \"Florence + the Machine\" classified as {expectedBucket}, as computed");
    Assert(
        florenceResult.TargetId == FixtureMusicBrainzApiClient.MbidFlorence,
        $"\"{displayName}\" correctly generates and identifies the Florence + the Machine candidate");
    Assert(
        florenceResult.Status == "auto_accept",
        $"\"{displayName}\" resolves to auto_accept -- name similarity plus a genuine Tier 1 track/album match together cross the threshold (got {florenceResult.Status})");
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
        Credit("ds-thats-it-i-quit", "That's It, I Quit, I'm Moving On", "19", "album-19", "Composer", "Del Serino", "emby-artist-del-serino")
    }
};
var delSerinoResult = engine.ResolveOne(delSerinoArtist, context);
Assert(delSerinoResult.Status == "needs_review", $"Del Serino lands on needs_review, same structural gap as Gus Black (got {delSerinoResult.Status})");
Assert(delSerinoResult.TargetId != FixtureMusicBrainzApiClient.MbidDelSerino, "Del Serino's own MBID is never considered as a candidate today, for the same reason as Gus Black");

Console.WriteLine($"\n=== {(failures == 0 ? "ALL PASS" : failures + " FAILURE(S)")} ===");

return failures == 0 ? 0 : 1;
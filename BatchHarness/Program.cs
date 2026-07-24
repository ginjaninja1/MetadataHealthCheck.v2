using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MetadataHealthCheck.v2.BatchHarness;
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

// Headless batch runner: same real engine, real ScoringConfig, and real
// HttpMusicBrainzApiClient as SmokeTest, but with no per-artist pause and
// bounded parallelism, over an extracted dataset rather than one hand-built
// observations.txt. Purpose: measure accuracy (against known-correct MBIDs)
// and API/time efficiency across a large artist set in one run, not to
// support interactive stepping through any single artist's trace -- use
// SmokeTest for that.
//
// THREAD SAFETY: HttpMusicBrainzApiClient and InMemoryIdentityCache both use
// plain (non-concurrent) Dictionary fields internally with no locking --
// confirmed by reading both classes directly, not assumed. Sharing either
// across concurrent artists would risk corrupting their internal caches and
// call counters. Instead, EACH concurrent worker gets its own client and
// identity cache (see BuildStack() below) -- no shared mutable state crosses
// a thread boundary. This costs some cache reuse across artists (an artist
// name/alias looked up by worker 1 isn't visible to worker 2), which is an
// acceptable trade for correctness; ScoringConfig itself IS shared across all
// workers since it's read-only settings data (confirmed: no field on it is
// ever written to after construction).
//
// If HttpMusicBrainzApiClient/InMemoryIdentityCache are ever made genuinely
// thread-safe (ConcurrentDictionary + Interlocked), a single shared instance
// could be used instead for better cache reuse -- flagged in EvidenceLog.md
// as a real future item, not done here since it touches shared production
// code for the sake of this one benchmarking tool.

if (args.Length < 2)
{
    Console.WriteLine("Usage: BatchHarness <observations.txt path> <groundtruth.csv path> [outputCsvPath] [maxConcurrency]");
    return 1;
}

var observationsPath = args[0];
var groundTruthPath = args[1];
var outputCsvPath = args.Length > 2 ? args[2] : "batch-results.csv";
// Default of 8: musicbrainz.emby.tv has no enforced rate limit (confirmed by
// Nick), so the ceiling here is really about not hammering the mirror harder
// than a considerate batch job should, and about giving a visible knob to
// taper down if connection errors/timeouts show up in a run. Not tuned
// against measured throughput yet -- treat as a starting point, not a
// verified optimum.
var maxConcurrency = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 8;

if (!File.Exists(observationsPath))
{
    Console.WriteLine($"FATAL: observations file not found: {observationsPath}");
    return 1;
}
if (!File.Exists(groundTruthPath))
{
    Console.WriteLine($"FATAL: ground truth file not found: {groundTruthPath}");
    return 1;
}

var groundTruth = LoadGroundTruth(groundTruthPath);
Console.WriteLine($"Loaded {groundTruth.Count} ground-truth row(s) from {groundTruthPath}.");

var reader = new TextFileEmbyLibraryReader(observationsPath);
var provider = new EmbyArtistProvider(reader);
var context = new ResolutionContext();
var artists = provider.GetAll(context).ToList();
Console.WriteLine($"Loaded {artists.Count} artist(s) from {observationsPath}.");
Console.WriteLine($"Running with max concurrency = {maxConcurrency}.\n");

var scoringConfig = new ScoringConfig(); // shared: read-only, safe across workers -- see header note
var rows = new System.Collections.Concurrent.ConcurrentBag<BatchResultRow>();
var overallStopwatch = Stopwatch.StartNew();
var completed = 0;

await Parallel.ForEachAsync(
    artists,
    new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency },
    async (artist, ct) =>
    {
        var row = new BatchResultRow
        {
            ArtistName = artist.DisplayName,
            SourceId = artist.SourceId,
            ExpectedMbid = groundTruth.TryGetValue(artist.SourceId, out var expected) ? expected : "",
        };

        var (logger, mbClient, identityCache, plugin) = BuildStack();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var repo = new InMemoryMatchRepository();
            var engine = new ResolutionEngine(plugin, repo, identityCache, scoringConfig, logger);
            var result = engine.ResolveOne(artist, context);

            row.ChosenMbid = result.TargetId;
            row.Decision = result.Status;
            row.Confidence = result.Confidence;
            row.Margin = result.Margin;
        }
        catch (Exception ex)
        {
            row.Error = ex.Message;
        }
        finally
        {
            stopwatch.Stop();
            row.ElapsedMs = stopwatch.ElapsedMilliseconds;
            row.ApiCalls = mbClient.TotalApiCalls;
            mbClient.Dispose();
        }

        rows.Add(row);
        var done = Interlocked.Increment(ref completed);
        if (done % 25 == 0 || done == artists.Count)
            Console.WriteLine($"  ... {done}/{artists.Count} artists processed ({overallStopwatch.Elapsed:mm\\:ss} elapsed)");

        await Task.CompletedTask;
    });

overallStopwatch.Stop();
var resultRows = rows.OrderBy(r => r.ArtistName, StringComparer.OrdinalIgnoreCase).ToList();

AccuracyReport.WriteCsv(outputCsvPath, resultRows);
Console.WriteLine($"\nDetail CSV written to {outputCsvPath}");
Console.WriteLine($"Total wall time: {overallStopwatch.Elapsed:mm\\:ss}");
Console.WriteLine($"Total live MBZ API calls across all workers: {resultRows.Sum(r => r.ApiCalls)}");

AccuracyReport.PrintConsoleSummary(resultRows);

return 0;

// ---------------------------------------------------------------------------

static (StructuredLogger logger, HttpMusicBrainzApiClient mbClient, InMemoryIdentityCache identityCache, MusicBrainzArtistResolverPlugin plugin) BuildStack()
{
    // Fresh logger too, not just for thread safety -- StructuredLogger.Lines is
    // also a plain, unlocked List<string> (confirmed by reading Diagnostics/
    // StructuredLogger.cs), same category of problem as the two caches above.
    var logger = new StructuredLogger();
    var mbClient = new HttpMusicBrainzApiClient(logger);
    var identityCache = new InMemoryIdentityCache();
    var scoringConfigForPlugin = new ScoringConfig(); // plugin ctor requires one; shared config values are identical to the outer one
    var plugin = new MusicBrainzArtistResolverPlugin(mbClient, identityCache, scoringConfigForPlugin, logger);
    return (logger, mbClient, identityCache, plugin);
}

static Dictionary<string, string> LoadGroundTruth(string path)
{
    // ArtistSourceId,ArtistName,KnownMbid -- matches BenchmarkExtractionTask's
    // sidecar CSV. Minimal RFC4180-ish parsing: only handles the double-quote
    // escaping BenchmarkExtractionTask's own CsvEscape produces (quotes a field
    // containing a comma or quote, doubling embedded quotes) -- not a general
    // CSV parser, deliberately, since this only ever reads that one writer's
    // own output.
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var lines = File.ReadAllLines(path);
    for (int i = 1; i < lines.Length; i++) // skip header
    {
        var line = lines[i];
        if (string.IsNullOrWhiteSpace(line)) continue;
        var fields = ParseCsvLine(line);
        if (fields.Count < 3) continue;
        result[fields[0]] = fields[2];
    }
    return result;
}

static List<string> ParseCsvLine(string line)
{
    var fields = new List<string>();
    int i = 0;
    while (i <= line.Length)
    {
        string field;
        if (i < line.Length && line[i] == '"')
        {
            var sb = new System.Text.StringBuilder();
            i++;
            while (i < line.Length)
            {
                if (line[i] == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                if (line[i] == '"') { i++; break; }
                sb.Append(line[i]);
                i++;
            }
            field = sb.ToString();
            if (i < line.Length && line[i] == ',') i++;
            else i++;
        }
        else
        {
            var next = line.IndexOf(',', i);
            if (next < 0) { field = line.Substring(i); i = line.Length + 1; }
            else { field = line.Substring(i, next - i); i = next + 1; }
        }
        fields.Add(field);
        if (i > line.Length) break;
    }
    return fields;
}
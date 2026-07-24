using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace MetadataHealthCheck.v2.Tasks
{
    /// <summary>
    /// Extracts a benchmarking dataset from the live Emby library for
    /// MetadataHealthCheck.v2's batch accuracy harness (BatchHarness/).
    ///
    /// This is the first real (non-fixture) implementation of the "E2 fat query"
    /// described in IEmbyLibraryReader.cs's doc comment -- until now the only
    /// IEmbyLibraryReader implementation was TextFileEmbyLibraryReader, reading
    /// hand-built sample data. This task is deliberately narrow: it does NOT
    /// implement IEmbyLibraryReader itself or feed the live engine directly. It
    /// writes the SAME plain-text format TextFileEmbyLibraryReader already parses
    /// (see that file's FILE FORMAT doc comment), so the extracted dataset is
    /// replayable through the existing, already-tested parsing/model/engine
    /// pipeline unchanged. A live IEmbyLibraryReader (for real-time resolution
    /// inside Emby, as opposed to benchmarking) is a separate, not-yet-built
    /// piece of work.
    ///
    /// Ground truth (each artist's already-confirmed correct MBID) is written to
    /// a SEPARATE sidecar CSV, not inlined into the observations text file. The
    /// observations format has no line the existing TextFileEmbyLibraryReader
    /// parser ignores -- adding an inline "KNOWN_MBID" line would either throw
    /// (parser has no catch-all skip) or require changing that shared parser for
    /// the sake of this one benchmarking consumer. A sidecar file keyed by
    /// ArtistSourceId avoids touching shared parsing code entirely.
    ///
    /// ASSUMPTION FLAGGED FOR VERIFICATION: this task queries tracks for an
    /// artist via InternalItemsQuery.ArtistIds = [artist.InternalId]. This is the
    /// exact query MusicArtist.GetTaggedItemsResult itself builds (confirmed via
    /// ILSpy against MusicArtist.cs), which is Emby's own "this artist's page"
    /// query -- assumed to mean "any track this artist appears on in any role
    /// (AlbumArtist/Artist/Composer)", not "Artist-role only". This assumption
    /// needs verifying against a real composer-only-credited artist in your
    /// library (one with no AlbumArtist/Artist credits at all) -- if such an
    /// artist comes back with zero tracks from this query, the assumption is
    /// wrong and a separate ComposerArtistIds/AlbumArtistIds query will be
    /// needed instead of (or alongside) ArtistIds.
    ///
    /// KNOWN LIMITATION: names containing a literal double-quote character are
    /// sanitized (quote stripped) before being written, since the observations
    /// text format has no escape mechanism for embedded quotes and would fail
    /// to parse otherwise. Rare in practice but worth knowing about if a track
    /// count looks short.
    /// </summary>
    public class BenchmarkExtractionTask : IScheduledTask
    {
        private readonly ILibraryManager _library;
        private readonly ILogger _log;

        public BenchmarkExtractionTask(ILibraryManager libraryManager, ILogManager logManager)
        {
            _library = libraryManager;
            _log = logManager.GetLogger("MetadataHealthCheck.BenchmarkExtraction");
        }

        public string Key => "MetadataHealthCheckBenchmarkExtraction";
        public string Name => "MetadataHealthCheck: Extract Benchmark Dataset";
        public string Description =>
            "Extracts artist/track observation data and known-correct MusicBrainz artist IDs " +
            "from this Emby library into files consumable by MetadataHealthCheck.v2's offline " +
            "batch accuracy harness. Does not modify the library. Intended to be run manually " +
            "(no default schedule) against a curated library, or against an uncurated one to " +
            "capture native Emby's own resolved MBIDs for comparison.";
        public string Category => "GinjaNinja Tools";

        // No default trigger -- this is a manual, on-demand benchmarking action,
        // not a recurring library-maintenance task. An empty trigger list is
        // valid for IScheduledTask; the task still appears in the Scheduled
        // Tasks UI and can be run on demand.
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        // FLAGGED DECISION: no plugin Configuration class exists yet anywhere in
        // this repo (checked -- there is no Plugin.cs), so there is nowhere to
        // read an output path from. Following the same "constructor stand-in for
        // a not-yet-built config field" pattern already used in
        // EmbyArtistProvider (see its _artistFilter comment), this task writes to
        // a fixed pair of filenames under Emby's ProgramDataPath. Revisit once
        // real plugin configuration exists -- at that point OutputDirectory
        // should become a configurable field, not a constant.
        private const string ObservationsFileName = "MetadataHealthCheck.benchmark.observations.txt";
        private const string GroundTruthFileName = "MetadataHealthCheck.benchmark.groundtruth.csv";

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            // See FLAGGED DECISION above -- no config-driven path available yet.
            var outputDir = AppDomain.CurrentDomain.BaseDirectory;
            var observationsPath = Path.Combine(outputDir, ObservationsFileName);
            var groundTruthPath = Path.Combine(outputDir, GroundTruthFileName);

            _log.Info($"Starting benchmark extraction. Observations -> {observationsPath} ; Ground truth -> {groundTruthPath}");

            var artists = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "MusicArtist" },
                Recursive = true,
            });

            _log.Info($"Found {artists.Length} MusicArtist item(s) to extract.");

            var observationsText = new StringBuilder();
            var groundTruthRows = new List<string> { "ArtistSourceId,ArtistName,KnownMbid" };

            int processed = 0;
            int tracksWritten = 0;
            int artistsWithNoTracks = 0;
            int artistsWithNoKnownMbid = 0;

            foreach (var artistItem in artists)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!(artistItem is MusicArtist artist))
                {
                    processed++;
                    continue;
                }

                var sourceId = artist.Id.ToString("N", CultureInfo.InvariantCulture);
                var displayName = artist.Name ?? "Unknown Artist";
                var knownMbid = artist.GetProviderId(MetadataProviders.MusicBrainzArtist);

                if (string.IsNullOrWhiteSpace(knownMbid))
                {
                    artistsWithNoKnownMbid++;
                    _log.Debug($"[{displayName}] has no MusicBrainzArtist provider id -- will be extracted with an empty ground-truth MBID.");
                }

                var tracks = _library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Audio" },
                    ArtistIds = new[] { artist.InternalId },
                    Recursive = true,
                });

                if (tracks.Length == 0)
                {
                    artistsWithNoTracks++;
                    _log.Debug($"[{displayName}] has 0 tracks -- writing artist block with no TRACK entries.");
                }

                observationsText.AppendLine($"ARTIST {sourceId} \"{Sanitize(displayName)}\"");

                foreach (var trackItem in tracks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!(trackItem is Audio track)) continue;

                    var trackId = track.Id.ToString("N", CultureInfo.InvariantCulture);
                    observationsText.AppendLine($"TRACK {trackId} \"{Sanitize(track.Name ?? "")}\"");

                    var albumId = track.AlbumId.Equals(0L) ? "" : track.AlbumId.ToString(CultureInfo.InvariantCulture);
                    observationsText.AppendLine($"ALBUM {albumId} \"{Sanitize(track.Album ?? "")}\"");

                    if (track.RunTimeTicks.HasValue)
                    {
                        var ms = (long)(track.RunTimeTicks.Value / TimeSpan.TicksPerMillisecond);
                        observationsText.AppendLine($"DURATION_MS {ms}");
                    }

                    // The TRACK's own tags (Tier 0 concept) -- distinct from the
                    // AlbumArtist/Artist/Composer credit lines below. Written only
                    // if present; an empty ProviderIds dictionary produces no line
                    // at all (TextFileEmbyLibraryReader's PROVIDERIDS is optional).
                    if (track.ProviderIds != null && track.ProviderIds.Count > 0)
                    {
                        var pairs = track.ProviderIds
                            .Select(kv => $"{kv.Key}={kv.Value}")
                            .ToArray();
                        observationsText.AppendLine($"PROVIDERIDS {string.Join(",", pairs)}");
                    }

                    AppendCreditedNames(observationsText, "ALBUMARTIST", track.AlbumArtistItems);
                    AppendCreditedNames(observationsText, "ARTIST", track.ArtistItems);
                    AppendCreditedNames(observationsText, "COMPOSER", track.Composers);

                    observationsText.AppendLine(); // blank line closes the TRACK block
                    tracksWritten++;
                }

                observationsText.AppendLine(); // blank line closes the ARTIST block (harmless if tracks.Length==0)
                groundTruthRows.Add($"{sourceId},{CsvEscape(displayName)},{knownMbid ?? ""}");

                processed++;
                if (artists.Length > 0)
                    progress.Report(100.0 * processed / artists.Length);

                if (processed % 100 == 0)
                    _log.Info($"Progress: {processed} of {artists.Length} artists extracted...");
            }

            await Task.Run(() =>
            {
                File.WriteAllText(observationsPath, observationsText.ToString(), Encoding.UTF8);
                File.WriteAllLines(groundTruthPath, groundTruthRows, Encoding.UTF8);
            }, cancellationToken).ConfigureAwait(false);

            progress.Report(100);
            _log.Info(
                $"Extraction complete. Artists={processed} Tracks={tracksWritten} " +
                $"ArtistsWithNoTracks={artistsWithNoTracks} ArtistsWithNoKnownMbid={artistsWithNoKnownMbid}. " +
                $"Files written: {observationsPath} ; {groundTruthPath}");
        }

        private static void AppendCreditedNames(StringBuilder sb, string keyword, IEnumerable<MediaBrowser.Model.Dto.LinkedItemInfo> credits)
        {
            if (credits == null) return;
            foreach (var credit in credits)
            {
                var mbid = credit.ProviderIds != null && credit.ProviderIds.TryGetValue(
                    MetadataProviders.MusicBrainzArtist.ToString().ToLowerInvariant(), out var v) ? v : null;
                // netstandard2.0: ProviderIdDictionary's own key casing is unverified via
                // ILSpy -- MusicArtist.GetUserDataKeyInternal uses the this.GetProviderId(
                // MetadataProviders...) extension method rather than a raw dictionary index,
                // so the exact stored key string (enum name? lowercased? int?) hasn't been
                // directly confirmed for LinkedItemInfo.ProviderIds specifically. Flagged --
                // verify this lookup actually finds populated MBIDs against a real track with
                // known multi-artist credits before trusting this in a benchmark run; if it
                // comes back empty where it shouldn't, the artist-side extension method
                // pattern is the safer bet and this raw dictionary read should be replaced.
                var suffix = string.IsNullOrEmpty(mbid) ? "" : $" mbid={mbid}";
                sb.AppendLine($"{keyword} \"{Sanitize(credit.Name ?? "")}\"{suffix}");
            }
        }

        private static string Sanitize(string name) => name.Replace("\"", "");

        private static string CsvEscape(string field)
        {
            if (field.Contains(",") || field.Contains("\""))
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            return field;
        }
    }
}
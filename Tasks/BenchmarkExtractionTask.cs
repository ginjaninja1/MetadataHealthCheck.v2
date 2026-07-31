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
    /// RESOLVED (was an open question, confirmed against real library data): this
    /// task queries ArtistIds, AlbumArtistIds, and ComposerArtistIds separately
    /// and unions the results by track id -- ArtistIds alone does NOT return
    /// tracks where an artist's only credit is AlbumArtist or Composer. The union
    /// is confirmed working (real output shows ALBUMARTIST/ARTIST/COMPOSER lines
    /// all populating correctly for a real artist with mixed credits).
    ///
    /// BEST-3-TRACKS SELECTION (Nick's explicit spec, distinct from and simpler
    /// than the full five-rule §5.3.1 distance-seeking order used by
    /// EmbyArtistObservationUnitProvider at ENGINE RUNTIME -- this selection is
    /// ONLY for deciding what goes into the benchmark dataset, not a change to
    /// how the live engine samples):
    ///   1. Bucket priority: fill from AlbumArtist-tier tracks first, then
    ///      Artist-tier, then Composer-tier. Only move to the next tier if the
    ///      current tier can't supply enough DISTINCT albums to fill the
    ///      remaining slots -- never drop a tier "for variety" if it doesn't
    ///      have to.
    ///   2. Within a tier, albums are NEVER repeated -- at most one track per
    ///      album, ever. If a tier doesn't have 3 distinct albums, this artist
    ///      gets fewer than 3 tracks (no fallback to a second track from an
    ///      already-used album).
    ///   3. Among candidate albums, rank by: fewest total tracks in the album
    ///      first (a true per-album count, via a cached AlbumIds lookup -- NOT
    ///      "how many of this artist's own tracks are on it"), then longest
    ///      album name.
    ///   4. From the chosen album, take the single track with the longest
    ///      track name.
    /// This deliberately supersedes rules 2 and 3 of the original five-rule
    /// scheme (single-credit-first, different-title-first) for THIS dataset's
    /// purposes -- the distinct-albums-only constraint makes them redundant
    /// here. Flagged explicitly rather than silently merged, since it's a real
    /// narrowing of the original spec's ordering rules for this one use.
    ///
    /// KNOWN LIMITATION: names containing a literal double-quote character are
    /// sanitized (quote stripped) before being written, since the observations
    /// text format has no escape mechanism for embedded quotes and would fail
    /// to parse otherwise.
    ///
    /// KNOWN LIMITATION: the credited-name -> MBID lookup (for ALBUMARTIST/
    /// ARTIST/COMPOSER lines) is built from a name->MBID dictionary populated
    /// from this same run's full MusicArtist enumeration, keyed case-
    /// insensitively by display name. Two distinct real-world artists sharing an
    /// identical display name would collide in this dictionary. Acceptable for
    /// benchmarking; worth knowing about if a composer's mbid ever looks wrong.
    ///
    /// ASSUMPTION FLAGGED, NOT ILSPY-CONFIRMED: bucket classification (which of
    /// AlbumArtist/Artist/Composer a track counts as, for THIS artist) matches
    /// credited names to this artist via credit.Id == artist.InternalId, relying
    /// on LinkedItemInfo's constructor copying Id from the linked entity's own
    /// NameLongIdPair.Id. NameLongIdPair itself hasn't been directly inspected.
    /// If bucket classification looks wrong against real output, check this
    /// first.
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
        // not a recurring library-maintenance task.
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        // FLAGGED DECISION: no plugin Configuration class exists yet anywhere in
        // this repo, so there is nowhere to read an output path from. This task
        // writes to a fixed pair of filenames under AppDomain.CurrentDomain.
        // BaseDirectory. Revisit once real plugin configuration exists.
        private const string ObservationsFileName = "MetadataHealthCheck.benchmark.observations.txt";
        private const string GroundTruthFileName = "MetadataHealthCheck.benchmark.groundtruth.csv";

        // Nick's explicit spec: max 3 tracks TOTAL per artist for this benchmark
        // dataset -- distinct from (and much smaller than) ArtistMusicBrainzConfig's real
        // runtime BucketCeiling (3/4/6), which governs how many rounds the LIVE
        // engine may sample before escalating tiers during actual resolution.
        // This constant is purely about keeping the benchmark set small/focused
        // on best-case data, not a claim about engine sampling behaviour.
        private const int MaxTracksPerArtist = 3;

        private static readonly string[] BucketPriority = { "AlbumArtist", "Artist", "Composer" };

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
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

            // Built once, up front: name -> MBID, so credited names on OTHER
            // artists' tracks (album artists, co-artists, composers) can be
            // resolved to an MBID without a separate lookup per credit -- see
            // KNOWN LIMITATION above re: name collisions.
            var nameToMbid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in artists)
            {
                if (a is MusicArtist ma)
                {
                    var mbid = ma.GetProviderId(MetadataProviders.MusicBrainzArtist);
                    if (!string.IsNullOrWhiteSpace(mbid) && !string.IsNullOrWhiteSpace(ma.Name))
                        nameToMbid[ma.Name] = mbid;
                }
            }

            var observationsText = new StringBuilder();
            var groundTruthRows = new List<string> { "ArtistSourceId,ArtistName,KnownMbid" };
            var albumTrackCountCache = new Dictionary<long, int>();

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

                var artistIdArray = new[] { artist.InternalId };
                var asArtist = _library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Audio" },
                    ArtistIds = artistIdArray,
                    Recursive = true,
                });
                var asAlbumArtist = _library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Audio" },
                    AlbumArtistIds = artistIdArray,
                    Recursive = true,
                });
                var asComposer = _library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Audio" },
                    ComposerArtistIds = artistIdArray,
                    Recursive = true,
                });

                var allTracks = asArtist
                    .Concat(asAlbumArtist)
                    .Concat(asComposer)
                    .GroupBy(t => t.InternalId)
                    .Select(g => g.First())
                    .OfType<Audio>()
                    .ToArray();

                if (allTracks.Length == 0)
                {
                    artistsWithNoTracks++;
                    _log.Debug($"[{displayName}] has 0 tracks -- writing artist block with no TRACK entries.");
                }

                var selectedTracks = SelectBestTracks(allTracks, artist.InternalId, MaxTracksPerArtist, albumTrackCountCache);

                observationsText.AppendLine($"ARTIST {sourceId} \"{Sanitize(displayName)}\"");

                foreach (var track in selectedTracks)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var trackId = track.Id.ToString("N", CultureInfo.InvariantCulture);
                    observationsText.AppendLine($"TRACK {trackId} \"{Sanitize(track.Name ?? "")}\"");

                    var albumId = track.AlbumId.Equals(0L) ? "" : track.AlbumId.ToString(CultureInfo.InvariantCulture);
                    observationsText.AppendLine($"ALBUM {albumId} \"{Sanitize(track.Album ?? "")}\"");

                    if (track.RunTimeTicks.HasValue)
                    {
                        var ms = (long)(track.RunTimeTicks.Value / TimeSpan.TicksPerMillisecond);
                        observationsText.AppendLine($"DURATION_MS {ms}");
                    }

                    // The TRACK's own tags (Tier 0 concept: album/release-group/
                    // track/whichever-single-artist-Emby-already-resolved) --
                    // distinct from the ALBUMARTIST/ARTIST/COMPOSER credit lists
                    // below, which represent every credited name, not just what's
                    // already tagged directly on the track.
                    if (track.ProviderIds != null && track.ProviderIds.Count > 0)
                    {
                        var pairs = track.ProviderIds
                            .Select(kv => $"{kv.Key}={kv.Value}")
                            .ToArray();
                        observationsText.AppendLine($"PROVIDERIDS {string.Join(",", pairs)}");
                    }

                    AppendCreditedNames(observationsText, "ALBUMARTIST", track.AlbumArtistItems, nameToMbid);
                    AppendCreditedNames(observationsText, "ARTIST", track.ArtistItems, nameToMbid);
                    AppendCreditedNames(observationsText, "COMPOSER", track.Composers, nameToMbid);

                    observationsText.AppendLine(); // blank line closes the TRACK block
                    tracksWritten++;
                }

                observationsText.AppendLine(); // blank line closes the ARTIST block (harmless if selectedTracks is empty)
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

        /// <summary>
        /// Implements the best-3-tracks cascade described in this class's header
        /// comment. See there for the full rationale; this method is the literal
        /// implementation of steps 1-4.
        /// </summary>
        private List<Audio> SelectBestTracks(Audio[] candidateTracks, long artistInternalId, int maxTracks, Dictionary<long, int> albumTrackCountCache)
        {
            var selected = new List<Audio>();
            var usedAlbumIds = new HashSet<long>();
            // Tracks with no real album link (AlbumId == 0) must never be treated
            // as "the same album" as each other -- each gets its own synthetic,
            // never-repeating negative key so they're always eligible, never
            // falsely excluded as a duplicate of some other album-less track.
            long syntheticAlbumIdCounter = -1;

            foreach (var bucket in BucketPriority)
            {
                if (selected.Count >= maxTracks) break;

                var bucketTracks = candidateTracks
                    .Where(t => TrackBucketFor(t, artistInternalId) == bucket)
                    .ToList();
                if (bucketTracks.Count == 0) continue;

                var albumGroups = bucketTracks
                    .GroupBy(t => t.AlbumId.Equals(0L) ? (syntheticAlbumIdCounter--) : t.AlbumId)
                    .Where(g => !usedAlbumIds.Contains(g.Key))
                    .Select(g => new
                    {
                        AlbumId = g.Key,
                        Tracks = g.ToList(),
                        TrackCount = g.Key < 0 ? 1 : GetAlbumTrackCount(g.Key, albumTrackCountCache),
                        AlbumNameLength = (g.First().Album ?? "").Length,
                    })
                    .OrderBy(x => x.TrackCount)
                    .ThenByDescending(x => x.AlbumNameLength)
                    .ToList();

                int remaining = maxTracks - selected.Count;
                foreach (var albumGroup in albumGroups.Take(remaining))
                {
                    var bestTrack = albumGroup.Tracks
                        .OrderByDescending(t => (t.Name ?? "").Length)
                        .First();
                    selected.Add(bestTrack);
                    usedAlbumIds.Add(albumGroup.AlbumId);
                }
            }

            return selected;
        }

        /// <summary>
        /// Which bucket (AlbumArtist/Artist/Composer) this track counts as for
        /// THIS artist specifically -- a track can carry multiple simultaneous
        /// credits (e.g. be both AlbumArtist and Artist), so this returns the
        /// HIGHEST-priority bucket it qualifies for, matching §5.3's "highest
        /// signal first" ordering. See this class's ASSUMPTION FLAGGED comment
        /// re: the Id-matching this relies on.
        /// </summary>
        private static string TrackBucketFor(Audio track, long artistInternalId)
        {
            if (track.AlbumArtistItems != null && track.AlbumArtistItems.Any(c => c.Id == artistInternalId))
                return "AlbumArtist";
            if (track.ArtistItems != null && track.ArtistItems.Any(c => c.Id == artistInternalId))
                return "Artist";
            if (track.Composers != null && track.Composers.Any(c => c.Id == artistInternalId))
                return "Composer";
            return "Unknown"; // shouldn't happen -- track came from one of the three role queries
        }

        private int GetAlbumTrackCount(long albumId, Dictionary<long, int> cache)
        {
            if (cache.TryGetValue(albumId, out var cached))
                return cached;

            var count = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Audio" },
                AlbumIds = new[] { albumId },
                Recursive = true,
            }).Length;

            cache[albumId] = count;
            return count;
        }

        private static void AppendCreditedNames(StringBuilder sb, string keyword, IEnumerable<MediaBrowser.Model.Dto.LinkedItemInfo> credits, Dictionary<string, string> nameToMbid)
        {
            if (credits == null) return;
            foreach (var credit in credits)
            {
                // Prefer the credit's own ProviderIds if populated (hasn't been
                // seen populated in real data so far, but cheap to check first);
                // otherwise fall back to the name->MBID dictionary built from
                // this run's full artist enumeration. See KNOWN LIMITATION above
                // re: name-collision risk in that fallback.
                string mbid = null;
                if (credit.ProviderIds != null && credit.ProviderIds.TryGetValue(
                    MetadataProviders.MusicBrainzArtist.ToString().ToLowerInvariant(), out var direct))
                {
                    mbid = direct;
                }
                else if (!string.IsNullOrEmpty(credit.Name) && nameToMbid.TryGetValue(credit.Name, out var viaName))
                {
                    mbid = viaName;
                }

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
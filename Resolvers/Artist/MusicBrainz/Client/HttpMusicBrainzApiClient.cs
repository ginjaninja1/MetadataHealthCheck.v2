using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Diagnostics;
using MetadataHealthCheck.v2.Http;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Client.Model;

namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Client
{
    /// <summary>
    /// Live IMusicBrainzApiClient implementation, hitting the MusicBrainz mirror
    /// at https://musicbrainz.emby.tv/ws/2/ -- this mirror does not throttle,
    /// does not require a User-Agent, and does not require an API key.
    ///
    /// JSON parsing uses DataContractJsonSerializer (System.Runtime.Serialization.Json),
    /// which ships as part of the NETStandard.Library metapackage already
    /// referenced by this project, rather than System.Text.Json, which would
    /// require an additional NuGet package reference on netstandard2.0.
    ///
    /// Every call logs its outbound query and a summary of what came back, via
    /// StructuredLogger, so a smoke test run shows every lookup inline with the
    /// evidence/decision trace it fed.
    ///
    /// HTTP transport, response caching, and API-call counting all now live in
    /// CachedHttpApiClientBase -- this class supplies only its own call-name-
    /// to-cache-table map and TTL, plus the MusicBrainz-specific query/DTO
    /// logic. TotalApiCalls/ApiCallsByType (live calls) and
    /// TotalCacheHits/CacheHitsByType are inherited, not redeclared here.
    /// </summary>
    public class HttpMusicBrainzApiClient : CachedHttpApiClientBase, IMusicBrainzApiClient
    {
        private const string BaseUrl = "https://musicbrainz.emby.tv/ws/2/";

        // Every SearchArtist result is remembered here so GetArtistDisplayName/
        // GetArtistAliases can serve a candidate already seen this run without
        // an extra live call. Distinct from the persistent response cache in
        // CachedHttpApiClientBase: this is a same-instance, same-run,
        // parsed-object shortcut that avoids re-issuing a *different* call
        // (e.g. GetArtistDisplayName) whose answer SearchArtist already
        // incidentally returned -- the persistent cache only ever short-
        // circuits an identical call repeated.
        private readonly Dictionary<string, string> _knownArtistNames = new();
        private readonly Dictionary<string, List<string>> _knownArtistAliases = new();

        // Recording- and artist-scoped relationship lookups are remembered here
        // so two collectors needing the same recording's or artist's
        // relationships within one run never issue duplicate calls, same
        // rationale as _knownArtistNames/_knownArtistAliases above.
        private readonly Dictionary<string, IReadOnlyList<MbRelationship>> _knownRelationships = new();
        private readonly Dictionary<string, IReadOnlyList<MbArtistRelationship>> _knownArtistRelationships = new();

        // callName -> persistent cache table, one per distinct call type (see
        // ApiResponseCacheRepository doc comment for why per-call-type tables
        // rather than one shared table). A callName missing here is
        // deliberately uncached -- CachedHttpApiClientBase.Get logs a warning
        // rather than silently defaulting one in.
        private static readonly IReadOnlyDictionary<string, string> CacheTableMap = new Dictionary<string, string>
        {
            ["SearchArtist"] = "musicbrainz_artists",
            ["GetReleaseGroupTitles"] = "musicbrainz_releasegrouptitles",
            ["SearchRecording"] = "musicbrainz_recordings",
            ["SearchRecordingByTitleAndDuration"] = "musicbrainz_recordings_by_title_duration",
            ["GetRelationships"] = "musicbrainz_recordingswithrelationships",
            ["GetArtistDisplayName"] = "musicbrainz_artist_displayname",
            ["GetArtistAliases"] = "musicbrainz_artist_aliases",
            ["GetArtistRelationships"] = "musicbrainz_artistrelationships",
        };

        protected override IReadOnlyDictionary<string, string> CallNameToCacheTable => CacheTableMap;

        // MusicBrainz relationship/alias/search data changes, but rarely and
        // slowly for any given artist -- 30 days is a reasonable balance
        // between staleness risk and the load/latency saved during active
        // development, where the same artist set gets re-run often. Revisit
        // if MB data proves to churn faster than assumed here.
        protected override TimeSpan? DefaultCacheTtl => TimeSpan.FromDays(30);

        public HttpMusicBrainzApiClient(IApiResponseCache cache, StructuredLogger logger)
            : base(BaseUrl, cache, logger)
        {
        }

        // ---- C1: artist search ----------------------------------------------

        // The query actually used to produce the most recent SearchArtist result
        // set (primary or fallback rung -- see SearchArtist below). Lets
        // ArtistCandidateStrategy log/record the real query used rather than
        // assuming the primary one always was. Null before SearchArtist has
        // ever been called.
        public string? LastSearchArtistQueryUsed { get; private set; }

        public IReadOnlyList<MbArtistResult> SearchArtist(string name)
        {
            // Explicit alias search, not just relying on inline aliases carried
            // on name-matched results: alias hits score inherently lower in
            // MB's own relevance ranking than a direct name hit --
            // ArtistCandidateStrategy uses that score, plus which field
            // (name-vs-alias) actually matched, to decide sort tier.
            //
            // Query construction (primary query, fallback rung, nickname-quote
            // weighting) lives in MusicBrainzQueryBuilder, not here: this class
            // is transport (HTTP, DTOs, caching), not search-syntax strategy.
            var primaryQuery = MusicBrainzQueryBuilder.BuildArtistPrimaryQuery(name);
            var results = RunArtistSearch(primaryQuery, name);
            var queryUsed = primaryQuery;

            if (results.Count == 0)
            {
                var fallbackQuery = MusicBrainzQueryBuilder.BuildArtistFallbackQuery(name);
                Logger.Debug("MbApi", "  -> primary SearchArtist query returned 0 results, trying fallback rung: {0}", fallbackQuery);
                results = RunArtistSearch(fallbackQuery, name);
                queryUsed = fallbackQuery;
            }

            LastSearchArtistQueryUsed = queryUsed;
            return results;
        }

        private List<MbArtistResult> RunArtistSearch(string query, string name)
        {
            var url = $"artist?query={Uri.EscapeDataString(query)}&fmt=json&limit=25";
            var body = Get(url, "SearchArtist", $"name=\"{name}\"");
            var parsed = body == null ? null : DeserializeJson<ArtistSearchResponseDto>(body);

            var results = new List<MbArtistResult>();
            if (parsed?.Artists != null)
            {
                foreach (var a in parsed.Artists)
                {
                    var result = new MbArtistResult
                    {
                        Mbid = a.Id ?? "",
                        Name = a.Name ?? "",
                        Disambiguation = a.Disambiguation,
                        Score = ParseScore(a.Score),
                        Type = a.Type ?? "",
                    };
                    if (a.Aliases != null)
                        foreach (var al in a.Aliases)
                            if (!string.IsNullOrEmpty(al.Name))
                                result.Aliases.Add(al.Name!);
                    results.Add(result);
                    if (result.Mbid != "")
                    {
                        _knownArtistNames[result.Mbid] = result.Name;
                        _knownArtistAliases[result.Mbid] = result.Aliases.ToList();
                    }
                }
            }

            Logger.Debug("MbApi", "  -> {0} artist result(s):", results.Count);
            foreach (var r in results)
                Logger.Debug("MbApi", "       {0} [{1}] type={2} score={3} aliases=[{4}]", r.Name, r.Mbid, r.Type, r.Score, string.Join(", ", r.Aliases));
            return results;
        }

        // ---- C2 (subset): release-group titles for an artist -----------------

        public IReadOnlyList<MbAlbumTitle> GetReleaseGroupTitles(string artistMbid)
        {
            var url = $"release-group?artist={artistMbid}&fmt=json&limit=100";
            var body = Get(url, "GetReleaseGroupTitles", $"artistMbid={artistMbid}");
            var parsed = body == null ? null : DeserializeJson<ReleaseGroupBrowseDto>(body);

            var titles = new List<MbAlbumTitle>();
            if (parsed?.ReleaseGroups != null)
            {
                foreach (var g in parsed.ReleaseGroups)
                {
                    var title = g.Title ?? "";
                    var isCompilation = g.SecondaryTypes?.Any(t => string.Equals(t, "Compilation", StringComparison.OrdinalIgnoreCase)) == true;
                    var isGenericTitle = title.IndexOf("Greatest Hits", StringComparison.OrdinalIgnoreCase) >= 0
                        || title.IndexOf("Best of", StringComparison.OrdinalIgnoreCase) >= 0
                        || title.Equals("Anthology", StringComparison.OrdinalIgnoreCase);

                    titles.Add(new MbAlbumTitle { Title = title, IsDistinctive = !isCompilation && !isGenericTitle });
                }
            }

            Logger.Debug("MbApi", "  -> {0} release-group title(s), {1} flagged non-distinctive", titles.Count, titles.Count(t => !t.IsDistinctive));
            return titles;
        }

        // ---- C3/C4: recording search -----------------------------------------

        // Extracted 2026-07-24 so a cache layer sitting above this client (e.g.
        // RecordingLookup's own per-rung cache) can log the URL a cache hit avoided
        // calling, without duplicating this query-construction logic. This method
        // builds the query/URL/description only -- it makes no HTTP call itself.
        private (string Url, string CallDesc) BuildSearchRecordingQuery(string trackTitle, string? albumTitle, IEnumerable<string>? artistNames)
        {
            var query = MusicBrainzQueryBuilder.BuildRecordingSearchQuery(trackTitle, albumTitle, artistNames);
            var url = $"recording?query={Uri.EscapeDataString(query)}&fmt=json&limit=25";

            var artistNameList = (artistNames ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct()
                .ToList();
            var callDesc = $"track=\"{trackTitle}\" album=\"{albumTitle ?? "(none)"}\" artist=\"{(artistNameList.Count > 0 ? string.Join(" OR ", artistNameList) : "(none)")}\"";
            return (url, callDesc);
        }

        // Exposes the same query-building logic SearchRecording uses internally,
        // purely for logging purposes. Makes no HTTP call.
        public string DescribeSearchRecordingUrl(string trackTitle, string? albumTitle, IEnumerable<string>? artistNames = null)
        {
            var (url, _) = BuildSearchRecordingQuery(trackTitle, albumTitle, artistNames);
            return url;
        }

        // Extracts the recording/track-level artist credit, shared by
        // SearchRecording and SearchRecordingByTitleAndDuration. Reads
        // MusicBrainz's own "joinphrase" per credit for display, and collects
        // every credited artist's MBID/name into ArtistMbids/ArtistCreditNames
        // so a candidate credited 2nd/3rd on a multi-artist recording is still
        // visible to match confirmation. ArtistMbid (first-only) is kept
        // separately for the TrackDuration frequency tally, which groups by
        // first-artist-only as its own known, flagged simplification.
        private static (string ArtistMbid, string CreditText, List<string> ArtistMbids, List<string> ArtistCreditNames) ParseArtistCredit(List<ArtistCreditDto>? artistCredit)
        {
            var artistMbid = "";
            var artistMbids = new List<string>();
            var artistCreditNames = new List<string>();
            var sb = new StringBuilder();

            if (artistCredit != null)
            {
                foreach (var c in artistCredit)
                {
                    var name = c.Name ?? "";
                    sb.Append(name);
                    sb.Append(c.JoinPhrase ?? "");

                    if (c.Artist?.Id != null)
                    {
                        if (artistMbid == "") artistMbid = c.Artist.Id;
                        artistMbids.Add(c.Artist.Id);
                        artistCreditNames.Add(name);
                    }
                }
            }

            return (artistMbid, sb.ToString(), artistMbids, artistCreditNames);
        }

        // Extracts the release-level artist credit (MusicBrainz's actual "album
        // artist") from a representative release, shared by both SearchRecording
        // and SearchRecordingByTitleAndDuration. First-credited-artist only:
        // release-level "album artist" is typically single-valued in practice,
        // unlike track-level credits -- flag if that proves wrong.
        private static (string? Mbid, string? CreditText) ParseReleaseAlbumArtist(ReleaseDto? release)
        {
            if (release?.ArtistCredit == null || release.ArtistCredit.Count == 0)
                return (null, null);

            string? mbid = null;
            var names = new List<string>();
            foreach (var c in release.ArtistCredit)
            {
                names.Add(c.Name ?? "");
                if (mbid == null && c.Artist?.Id != null)
                    mbid = c.Artist.Id;
            }
            return (mbid, string.Join("", names));
        }

        // Unlike ParseReleaseAlbumArtist above (single representative release,
        // richness-ranking use only), scans every release's artist-credit and
        // returns the full distinct set of MBIDs found -- used for candidate
        // confirmation, where a match on any release counts, regardless of
        // which release happened to be picked as "representative". Names are
        // collected in the same pass purely for debug-log readability --
        // matching itself is done on Mbids only, never on Names.
        private static (List<string> Mbids, List<string> Names) ParseAllReleaseAlbumArtistMbids(List<ReleaseDto>? releases)
        {
            var mbids = new List<string>();
            var names = new List<string>();
            if (releases == null) return (mbids, names);

            foreach (var release in releases)
            {
                if (release.ArtistCredit == null) continue;
                foreach (var c in release.ArtistCredit)
                {
                    if (c.Artist?.Id != null && !mbids.Contains(c.Artist.Id))
                    {
                        mbids.Add(c.Artist.Id);
                        names.Add(c.Name ?? c.Artist.Name ?? "");
                    }
                }
            }
            return (mbids, names);
        }

        public IReadOnlyList<MbRecordingResult> SearchRecording(string trackTitle, string? albumTitle, IEnumerable<string>? artistNames = null)
        {
            var (url, callDesc) = BuildSearchRecordingQuery(trackTitle, albumTitle, artistNames);
            var body = Get(url, "SearchRecording", callDesc);
            var parsed = body == null ? null : DeserializeJson<RecordingSearchResponseDto>(body);

            var results = new List<MbRecordingResult>();
            if (parsed?.Recordings != null)
            {
                foreach (var r in parsed.Recordings)
                {
                    var recTitle = r.Title ?? "";
                    var releaseTitleMatches = !string.IsNullOrWhiteSpace(albumTitle)
                        && r.Releases != null
                        && r.Releases.Any(rel => (rel.Title ?? "").Equals(albumTitle, StringComparison.OrdinalIgnoreCase));

                    var (artistMbid, creditText, artistMbids, artistCreditNames) = ParseArtistCredit(r.ArtistCredit);

                    // Pick one representative release to source the richness fields
                    // from (a recording can appear on many releases with different
                    // status/type -- see MbRecordingResult doc comment for why these
                    // are richness-only, not correctness signals). Prefer the first
                    // "Official" release if one exists, since that's the one most
                    // likely to carry populated relationship data; otherwise just the
                    // first release returned. ReleaseCount is the true count of
                    // distinct releases this recording appears on (not any single
                    // release's own "count" field, which represents something else --
                    // how many times the recording's title occurs within that one
                    // release's own tracklist).
                    var representativeRelease = r.Releases?.FirstOrDefault(rel => rel.Status == "Official") ?? r.Releases?.FirstOrDefault();
                    var (releaseAlbumArtistMbid, releaseAlbumArtistCreditText) = ParseReleaseAlbumArtist(representativeRelease);
                    var (allReleaseAlbumArtistMbids, allReleaseAlbumArtistNames) = ParseAllReleaseAlbumArtistMbids(r.Releases);

                    results.Add(new MbRecordingResult
                    {
                        RecordingId = r.Id ?? "",
                        ArtistMbid = artistMbid,
                        ArtistMbids = artistMbids,
                        ArtistCreditNames = artistCreditNames,
                        TrackTitle = recTitle,
                        ReleaseTitle = albumTitle ?? "",
                        TrackTitleMatches = recTitle.Equals(trackTitle, StringComparison.OrdinalIgnoreCase),
                        ReleaseTitleMatches = releaseTitleMatches,
                        ArtistCreditText = creditText,
                        LengthMs = r.Length,
                        Score = ParseScore(r.Score),
                        ReleaseStatus = representativeRelease?.Status,
                        ReleaseGroupPrimaryType = representativeRelease?.ReleaseGroup?.PrimaryType,
                        ReleaseGroupSecondaryTypes = representativeRelease?.ReleaseGroup?.SecondaryTypes ?? new List<string>(),
                        ReleaseCount = r.Releases?.Count ?? 0,
                        ReleaseAlbumArtistMbid = releaseAlbumArtistMbid,
                        ReleaseAlbumArtistCreditText = releaseAlbumArtistCreditText,
                        ReleaseAlbumArtistMbids = allReleaseAlbumArtistMbids,
                        ReleaseAlbumArtistNames = allReleaseAlbumArtistNames,
                    });
                }
            }

            Logger.Debug("MbApi", "  -> {0} recording result(s):", results.Count);
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                Logger.Debug("MbApi", "       Recording #{0}", i + 1);
                Logger.Debug("MbApi", "         ID:     {0}", r.RecordingId);
                Logger.Debug("MbApi", "         Track:  \"{0}\" (matches queried title: {1})", r.TrackTitle, r.TrackTitleMatches);
                Logger.Debug("MbApi", "         Artist: {0}", r.ArtistCreditText);
                Logger.Debug("MbApi", "         Album:  \"{0}\" (matches queried album: {1})", r.ReleaseTitle, r.ReleaseTitleMatches);
                Logger.Debug("MbApi", "         Album Artist: {0}", r.ReleaseAlbumArtistNames.Count > 0 ? string.Join(", ", r.ReleaseAlbumArtistNames) : "(none found)");
                Logger.Debug("MbApi", "         Length: {0}  Status: {1}  Type: {2}  Releases: {3}  Score: {4}",
                    r.LengthMs.HasValue ? $"{r.LengthMs}ms" : "(none)", r.ReleaseStatus ?? "(none)", r.ReleaseGroupPrimaryType ?? "(none)", r.ReleaseCount, r.Score);
                // NOTE: no AlbumArtist or Relationship fields here -- a raw recording
                // search result doesn't carry either. AlbumArtist isn't a MusicBrainz
                // concept at the recording level at all (that's Emby's field, not
                // MB's); relationships only exist via a SEPARATE GetRelationships call
                // on this recording's ID, logged separately when/if that call happens.
            }
            return results;
        }

        // Added 2026-07-19 for the TrackDuration rung (§7.2 "Bohemian Rhapsody"
        // trace): title + qdur range search, used once album and artist have both
        // already failed as narrowing fields. limit=100 (not the usual 25) because
        // this rung's value depends on seeing the full narrowed result set, not a
        // partial page -- see interface doc comment.
        //
        // NOTE: ArtistMbid on each returned MbRecordingResult still only captures the
        // FIRST credited artist (same limitation as SearchRecording above, inherited
        // from the shared parsing below) -- multi-artist credits (e.g. a duet)
        // under-count their non-first contributors in any frequency tally built off
        // this result set. Flagged as a known simplification, not silently accepted;
        // widening MbRecordingResult to carry all credited artist MBIDs is a larger
        // structural change left for a deliberate follow-up, not bundled in here.
        // See BuildSearchRecordingQuery's doc comment above -- same rationale, added
        // at the same time (2026-07-24), for this rung's query instead.
        //
        // Query text itself (including qdur bucket math / AssumedMbQdurBucketSeconds)
        // now lives in MusicBrainzQueryBuilder -- extracted 2026-07-29, same
        // one-file-one-purpose rationale as SearchArtist's query construction above.
        private (string Url, string CallDesc, int Low, int High) BuildSearchRecordingByTitleAndDurationQuery(string trackTitle, int observedDurationMs, int qdurToleranceBuckets)
        {
            var (query, low, high) = MusicBrainzQueryBuilder.BuildRecordingByTitleAndDurationQuery(trackTitle, observedDurationMs, qdurToleranceBuckets);
            var url = $"recording?query={Uri.EscapeDataString(query)}&fmt=json&limit=100";
            var callDesc = $"track=\"{trackTitle}\" qdur=[{low} TO {high}] (observedMs={observedDurationMs})";
            return (url, callDesc, low, high);
        }

        public string DescribeSearchRecordingByTitleAndDurationUrl(string trackTitle, int observedDurationMs, int qdurToleranceBuckets)
        {
            var (url, _, _, _) = BuildSearchRecordingByTitleAndDurationQuery(trackTitle, observedDurationMs, qdurToleranceBuckets);
            return url;
        }

        public IReadOnlyList<MbRecordingResult> SearchRecordingByTitleAndDuration(string trackTitle, int observedDurationMs, int qdurToleranceBuckets)
        {
            var (url, callDesc, low, high) = BuildSearchRecordingByTitleAndDurationQuery(trackTitle, observedDurationMs, qdurToleranceBuckets);
            var body = Get(url, "SearchRecordingByTitleAndDuration", callDesc);
            var parsed = body == null ? null : DeserializeJson<RecordingSearchResponseDto>(body);

            var results = new List<MbRecordingResult>();
            if (parsed?.Recordings != null)
            {
                foreach (var r in parsed.Recordings)
                {
                    var (artistMbid, creditText, artistMbids, artistCreditNames) = ParseArtistCredit(r.ArtistCredit);

                    var representativeRelease = r.Releases?.FirstOrDefault(rel => rel.Status == "Official") ?? r.Releases?.FirstOrDefault();
                    var (releaseAlbumArtistMbid, releaseAlbumArtistCreditText) = ParseReleaseAlbumArtist(representativeRelease);
                    var (allReleaseAlbumArtistMbids, allReleaseAlbumArtistNames) = ParseAllReleaseAlbumArtistMbids(r.Releases);

                    results.Add(new MbRecordingResult
                    {
                        RecordingId = r.Id ?? "",
                        ArtistMbid = artistMbid,
                        ArtistMbids = artistMbids,
                        ArtistCreditNames = artistCreditNames,
                        TrackTitle = r.Title ?? "",
                        ReleaseTitle = "",
                        TrackTitleMatches = (r.Title ?? "").Equals(trackTitle, StringComparison.OrdinalIgnoreCase),
                        ReleaseTitleMatches = false,
                        ArtistCreditText = creditText,
                        LengthMs = r.Length,
                        Score = ParseScore(r.Score),
                        ReleaseStatus = representativeRelease?.Status,
                        ReleaseGroupPrimaryType = representativeRelease?.ReleaseGroup?.PrimaryType,
                        ReleaseGroupSecondaryTypes = representativeRelease?.ReleaseGroup?.SecondaryTypes ?? new List<string>(),
                        ReleaseCount = r.Releases?.Count ?? 0,
                        ReleaseAlbumArtistMbid = releaseAlbumArtistMbid,
                        ReleaseAlbumArtistCreditText = releaseAlbumArtistCreditText,
                        ReleaseAlbumArtistMbids = allReleaseAlbumArtistMbids,
                        ReleaseAlbumArtistNames = allReleaseAlbumArtistNames,
                    });
                }
            }

            Logger.Debug("MbApi", "  -> {0} recording result(s) within qdur:[{1} TO {2}]:", results.Count, low, high);
            return results;
        }

        // ---- C5: relationships (work-level + recording-level, one call) ------

        public IReadOnlyList<MbRelationship> GetRelationships(string recordingId)
        {
            var url = $"recording/{recordingId}?inc=work-rels+artist-rels+work-level-rels&fmt=json";

            if (_knownRelationships.TryGetValue(recordingId, out var cached))
            {
                Logger.Info("MbApi", "[GetRelationships] recordingId={0}", recordingId);
                Logger.Info("MbApi", "  GET https://musicbrainz.emby.tv/ws/2/{0}", url);
                Logger.Debug("MbApi", "  -> cached from an earlier call, no live call needed. {0} relationship(s).", cached.Count);
                return cached;
            }

            var body = Get(url, "GetRelationships", $"recordingId={recordingId}");
            var parsed = body == null ? null : DeserializeJson<RecordingRelationshipsDto>(body);

            var results = new List<MbRelationship>();
            if (parsed?.Relations != null)
            {
                foreach (var rel in parsed.Relations)
                {
                    if (rel.TargetType == "artist" && rel.Artist?.Id != null)
                    {
                        results.Add(new MbRelationship
                        {
                            RelationshipType = rel.Type ?? "",
                            ArtistMbid = rel.Artist.Id,
                            Level = RelationshipLevel.Recording,
                        });
                    }
                    else if (rel.TargetType == "work" && rel.Work?.Relations != null)
                    {
                        foreach (var workRel in rel.Work.Relations)
                        {
                            if (workRel.TargetType == "artist" && workRel.Artist?.Id != null)
                            {
                                results.Add(new MbRelationship
                                {
                                    RelationshipType = workRel.Type ?? "",
                                    ArtistMbid = workRel.Artist.Id,
                                    Level = RelationshipLevel.Work,
                                });
                            }
                        }
                    }
                }
            }

            Logger.Debug("MbApi", "  -> {0} relationship(s):", results.Count);
            foreach (var r in results)
                Logger.Debug("MbApi", "       {0}({1})={2}", r.RelationshipType, r.Level, r.ArtistMbid);
            _knownRelationships[recordingId] = results;
            return results;
        }

        // ---- Phase 1 additions: display name / aliases by MBID ---------------

        public string GetArtistDisplayName(string artistMbid)
        {
            var url = $"artist/{artistMbid}?fmt=json";

            if (_knownArtistNames.TryGetValue(artistMbid, out var cached))
            {
                Logger.Info("MbApi", "[GetArtistDisplayName] artistMbid={0}", artistMbid);
                Logger.Info("MbApi", "  GET https://musicbrainz.emby.tv/ws/2/{0}", url);
                Logger.Debug("MbApi", "  -> cached from an earlier SearchArtist result, no live call needed. name=\"{0}\"", cached);
                return cached;
            }

            var body = Get(url, "GetArtistDisplayName", $"artistMbid={artistMbid}");
            var parsed = body == null ? null : DeserializeJson<ArtistDto>(body);
            var name = parsed?.Name ?? "";
            Logger.Debug("MbApi", "  -> name=\"{0}\"", name);
            if (name != "") _knownArtistNames[artistMbid] = name;
            return name;
        }

        public IReadOnlyList<string> GetArtistAliases(string artistMbid)
        {
            if (_knownArtistAliases.TryGetValue(artistMbid, out var cached))
            {
                Logger.Debug("MbApi", "[GetArtistAliases] artistMbid={0} -- cached from an earlier SearchArtist result, no live call needed. {1} alias(es).", artistMbid, cached.Count);
                return cached;
            }

            var url = $"artist/{artistMbid}?inc=aliases&fmt=json";
            var body = Get(url, "GetArtistAliases", $"artistMbid={artistMbid}");
            var parsed = body == null ? null : DeserializeJson<ArtistDto>(body);

            var aliases = new List<string>();
            if (parsed?.Aliases != null)
                foreach (var al in parsed.Aliases)
                    if (!string.IsNullOrEmpty(al.Name))
                        aliases.Add(al.Name!);

            Logger.Debug("MbApi", "  -> {0} alias(es): {1}", aliases.Count, string.Join(", ", aliases));
            _knownArtistAliases[artistMbid] = aliases;
            return aliases;
        }

        // ---- artist-rels: artist-to-artist relationships ("is person", etc.) -----
        // Added 2026-07-18. Distinct from GetRelationships (C5) above, which is scoped
        // to a recordingId; this is scoped to an artistMbid. Direction is deliberately
        // not surfaced on MbArtistRelationship -- confirmed direction-agnostic via a
        // real two-artist round trip (see MbArtistRelationship's doc comment).

        public IReadOnlyList<MbArtistRelationship> GetArtistRelationships(string artistMbid)
        {
            if (_knownArtistRelationships.TryGetValue(artistMbid, out var cached))
            {
                Logger.Debug("MbApi", "[GetArtistRelationships] artistMbid={0} -- cached from an earlier call, no live call needed. {1} relation(s).", artistMbid, cached.Count);
                return cached;
            }

            var url = $"artist/{artistMbid}?inc=artist-rels&fmt=json";
            var body = Get(url, "GetArtistRelationships", $"artistMbid={artistMbid}");
            var parsed = body == null ? null : DeserializeJson<ArtistRelationsDto>(body);

            var results = new List<MbArtistRelationship>();
            if (parsed?.Relations != null)
            {
                foreach (var rel in parsed.Relations)
                {
                    if (rel.TargetType == "artist" && rel.Artist?.Id != null)
                    {
                        results.Add(new MbArtistRelationship
                        {
                            ArtistMbid = rel.Artist.Id,
                            ArtistName = rel.Artist.Name ?? "",
                            RelationshipType = rel.Type ?? "",
                            RelationshipTypeId = rel.TypeId ?? "",
                        });
                    }
                }
            }

            Logger.Debug("MbApi", "  -> {0} artist relation(s):", results.Count);
            foreach (var r in results)
                Logger.Debug("MbApi", "       type=\"{0}\" ({1}) -> \"{2}\" [{3}]", r.RelationshipType, r.RelationshipTypeId, r.ArtistName, r.ArtistMbid);
            _knownArtistRelationships[artistMbid] = results;
            return results;
        }

        // ---- shared plumbing ---------------------------------------------------
        // Get(...) itself (HTTP transport, response caching, live-call counting)
        // is inherited from CachedHttpApiClientBase -- see that class for the
        // 301-redirect-Location-header diagnostic note (added 2026-07-27) and
        // the rest of the transport logic previously here.

        private static T? DeserializeJson<T>(string json) where T : class
        {
            try
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
                var serializer = new DataContractJsonSerializer(typeof(T));
                return serializer.ReadObject(stream) as T;
            }
            catch
            {
                // Malformed/unexpected-shape response -- caller treats a null parse the
                // same as "no results", consistent with the Get() method's own null-on-
                // failure contract above.
                return null;
            }
        }

        private static int ParseScore(string? score)
            => int.TryParse(score, out var parsed) ? parsed : 0;

        // ---- DTOs (DataContractJsonSerializer needs [DataMember(Name=...)] for
        // every JSON key that isn't a valid C# identifier as-is, e.g. hyphenated
        // MusicBrainz field names like "artist-credit", "target-type") -----------

        [DataContract]
        private class ArtistSearchResponseDto
        {
            [DataMember(Name = "artists")] public List<ArtistDto>? Artists { get; set; }
        }

        [DataContract]
        private class ArtistDto
        {
            [DataMember(Name = "id")] public string? Id { get; set; }
            [DataMember(Name = "name")] public string? Name { get; set; }
            [DataMember(Name = "disambiguation")] public string? Disambiguation { get; set; }
            [DataMember(Name = "score")] public string? Score { get; set; } // MB returns this as a JSON string, not a number
            [DataMember(Name = "aliases")] public List<AliasDto>? Aliases { get; set; }
            [DataMember(Name = "type")] public string? Type { get; set; } // "Person" | "Group" | others -- added 2026-07-27
        }

        [DataContract]
        private class AliasDto
        {
            [DataMember(Name = "name")] public string? Name { get; set; }
        }

        [DataContract]
        private class ReleaseGroupBrowseDto
        {
            [DataMember(Name = "release-groups")] public List<ReleaseGroupDto>? ReleaseGroups { get; set; }
        }

        [DataContract]
        private class ReleaseGroupDto
        {
            [DataMember(Name = "title")] public string? Title { get; set; }
            [DataMember(Name = "secondary-types")] public List<string>? SecondaryTypes { get; set; }
            // Added 2026-07-18: richness signal only, see MbRecordingResult doc comment.
            [DataMember(Name = "primary-type")] public string? PrimaryType { get; set; }
        }

        [DataContract]
        private class RecordingSearchResponseDto
        {
            [DataMember(Name = "recordings")] public List<RecordingDto>? Recordings { get; set; }
        }

        [DataContract]
        private class RecordingDto
        {
            [DataMember(Name = "id")] public string? Id { get; set; }
            [DataMember(Name = "title")] public string? Title { get; set; }
            [DataMember(Name = "releases")] public List<ReleaseDto>? Releases { get; set; }
            [DataMember(Name = "artist-credit")] public List<ArtistCreditDto>? ArtistCredit { get; set; }
            // Added 2026-07-18: recording-level duration, ms -- see MbRecordingResult.LengthMs
            // doc comment for why this became the primary disambiguator over MB's own
            // (unhelpfully saturated, in the one sample checked) relevance score.
            [DataMember(Name = "length")] public int? Length { get; set; }
            // Added 2026-07-18: was silently unparsed until now -- see MbRecordingResult.Score.
            [DataMember(Name = "score")] public string? Score { get; set; } // MB returns this as a JSON string, not a number -- same quirk as artist search
        }

        [DataContract]
        private class ReleaseDto
        {
            [DataMember(Name = "title")] public string? Title { get; set; }
            // Added 2026-07-18: richness signals only, see MbRecordingResult doc comment.
            [DataMember(Name = "status")] public string? Status { get; set; }
            [DataMember(Name = "release-group")] public ReleaseGroupDto? ReleaseGroup { get; set; }
            // Added 2026-07-28: release-level artist-credit -- MusicBrainz's actual
            // "album artist" (distinct from RecordingDto.ArtistCredit above, which is
            // the recording/track-level credit). See MbRecordingResult.ReleaseAlbumArtistMbid.
            [DataMember(Name = "artist-credit")] public List<ArtistCreditDto>? ArtistCredit { get; set; }
        }

        [DataContract]
        private class ArtistCreditDto
        {
            [DataMember(Name = "name")] public string? Name { get; set; }
            [DataMember(Name = "artist")] public ArtistRefDto? Artist { get; set; }

            // Added 2026-07-28 (bugfix): MusicBrainz's own separator to place AFTER
            // this credit's name when rendering the full credit string (e.g. "",
            // " & ", " feat. "). Was previously never read, so multi-artist credits
            // were logged with names jammed together (e.g. "GorillazMos DefBobby
            // Womack").
            [DataMember(Name = "joinphrase")] public string? JoinPhrase { get; set; }
        }

        [DataContract]
        private class ArtistRefDto
        {
            [DataMember(Name = "id")] public string? Id { get; set; }
            [DataMember(Name = "name")] public string? Name { get; set; }
        }

        [DataContract]
        private class RecordingRelationshipsDto
        {
            [DataMember(Name = "relations")] public List<RelationDto>? Relations { get; set; }
        }

        [DataContract]
        private class RelationDto
        {
            [DataMember(Name = "target-type")] public string? TargetType { get; set; }
            [DataMember(Name = "type")] public string? Type { get; set; }
            [DataMember(Name = "artist")] public ArtistRefDto? Artist { get; set; }
            [DataMember(Name = "work")] public WorkDto? Work { get; set; }
        }

        [DataContract]
        private class WorkDto
        {
            [DataMember(Name = "id")] public string? Id { get; set; }
            [DataMember(Name = "relations")] public List<RelationDto>? Relations { get; set; }
        }

        // ---- artist-rels DTOs (2026-07-18) -------------------------------------
        // Separate from RecordingRelationshipsDto/RelationDto above: those model a
        // recording's relations block; this models an ARTIST's, which carries
        // "type-id" (a stable GUID) alongside "type" (the human-readable name) --
        // confirmed against a real artist/{mbid}?inc=artist-rels response earlier
        // this conversation. RelationDto (recording-scoped) doesn't carry type-id
        // today because nothing needed it there yet; kept as two separate DTOs
        // rather than widening RelationDto, to avoid touching the already-working
        // recording-relationship parsing path for an unrelated call.
        [DataContract]
        private class ArtistRelationsDto
        {
            [DataMember(Name = "relations")] public List<ArtistRelationDto>? Relations { get; set; }
        }

        [DataContract]
        private class ArtistRelationDto
        {
            [DataMember(Name = "type")] public string? Type { get; set; }
            [DataMember(Name = "type-id")] public string? TypeId { get; set; }
            [DataMember(Name = "target-type")] public string? TargetType { get; set; }
            [DataMember(Name = "artist")] public ArtistRefDto? Artist { get; set; }
        }
    }
}
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using MetadataHealthCheck.v2.Diagnostics;

namespace MetadataHealthCheck.v2.Resolvers.MusicBrainz.Client
{
    /// <summary>
    /// Real IMusicBrainzApiClient implementation, hitting the Emby-run MusicBrainz
    /// mirror (https://musicbrainz.emby.tv/ws/2/) per Nick's direction 2026-07-16:
    /// this mirror does not throttle, does not require a User-Agent, and does not
    /// require an API key. This REPLACES FixtureMusicBrainzApiClient entirely --
    /// fixture-based MB responses are retired from testing, live data only.
    ///
    /// REWRITTEN 2026-07-16 (build failure fix): originally used System.Text.Json
    /// (JsonDocument/JsonElement), which requires an explicit NuGet package on
    /// netstandard2.0 -- that package reference would not restore cleanly in
    /// Nick's environment even after repeated restore/rebuild/clean attempts. This
    /// version uses DataContractJsonSerializer instead (System.Runtime.Serialization.Json),
    /// which ships as part of the NETStandard.Library metapackage already
    /// referenced by this project -- confirmed present in the project's own
    /// project.assets.json (NETStandard.Library's ref list includes
    /// System.Runtime.Serialization.Json.dll). NO new package reference needed;
    /// the earlier System.Text.Json PackageReference should be removed from the
    /// .csproj since it's no longer used by anything.
    ///
    /// Every one of the six §7.2 calls (C1-C6, minus C6 which isn't used yet) logs
    /// its outbound query and a summary of what came back, via the same
    /// StructuredLogger the rest of the engine writes to -- so a smoke test run
    /// shows every MBZ lookup inline with the evidence/decision trace it fed.
    ///
    /// NOT independently verified against live responses by Claude -- no network
    /// path to musicbrainz.emby.tv from the sandbox. Nick verifies this by running
    /// SmokeTest locally and reading the trace output.
    /// </summary>
    public class HttpMusicBrainzApiClient : IMusicBrainzApiClient, IDisposable
    {
        private const string BaseUrl = "https://musicbrainz.emby.tv/ws/2/";

        private readonly HttpClient _http;
        private readonly StructuredLogger _logger;

        // Added 2026-07-16: GetArtistDisplayName was being called by SmokeTest's
        // post-run scoreboard purely for display, re-fetching a name SearchArtist
        // had ALREADY returned earlier in the same run for the same candidate --
        // a genuinely pointless extra live call (Nick caught this from the trace
        // output). Every SearchArtist result gets remembered here; GetArtistDisplayName
        // checks this cache before hitting the network at all.
        private readonly Dictionary<string, string> _knownArtistNames = new();
        private readonly Dictionary<string, List<string>> _knownArtistAliases = new();

        // Added 2026-07-16: GetRelationships had NO caching at all, unlike the name/
        // alias lookups above -- if two different evidence collectors both need a
        // recording's relationships (e.g. WorkRelationshipEvidenceCollector and a
        // recording-level sibling), each triggered its own identical live call for
        // the same recordingId. Cached here instead, at the one choke point every
        // caller shares, rather than in either collector.
        private readonly Dictionary<string, IReadOnlyList<MbRelationship>> _knownRelationships = new();

        // Added 2026-07-18, same caching rationale as _knownRelationships above, for
        // the new artist-scoped GetArtistRelationships call.
        private readonly Dictionary<string, IReadOnlyList<MbArtistRelationship>> _knownArtistRelationships = new();

        // Added 2026-07-16 (Nick's explicit request): the tester needs to show API
        // LOAD, not just accuracy -- how many live MBZ calls a resolution actually
        // costs, broken down by call type, including calls hidden inside
        // RecordingLookup's confirmation ladder that don't otherwise surface anywhere.
        public int TotalApiCalls { get; private set; }
        public Dictionary<string, int> ApiCallsByType { get; } = new();

        public HttpMusicBrainzApiClient(StructuredLogger logger)
        {
            _logger = logger;
            _http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        }

        public void Dispose() => _http.Dispose();

        // ---- C1: artist search ----------------------------------------------

        public IReadOnlyList<MbArtistResult> SearchArtist(string name)
        {
            // Rewritten 2026-07-18 per Nick's direction: explicit alias search, not
            // just relying on inline aliases carried on name-matched results. Alias
            // hits score inherently lower in MB's own relevance ranking than a direct
            // name hit -- ArtistStrategy uses that score, plus which field
            // (name-vs-alias) actually matched, to decide sort tier.
            var escaped = EscapeLucene(name);
            var query = $"(artist:\"{escaped}\" OR alias:\"{escaped}\")";
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

            _logger.Debug("MbApi", "  -> {0} artist result(s):", results.Count);
            foreach (var r in results)
                _logger.Debug("MbApi", "       {0} [{1}] score={2} aliases=[{3}]", r.Name, r.Mbid, r.Score, string.Join(", ", r.Aliases));
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

            _logger.Debug("MbApi", "  -> {0} release-group title(s), {1} flagged non-distinctive", titles.Count, titles.Count(t => !t.IsDistinctive));
            return titles;
        }

        // ---- C3/C4: recording search -----------------------------------------

        public IReadOnlyList<MbRecordingResult> SearchRecording(string trackTitle, string? albumTitle, string? artistName = null)
        {
            var parts = new List<string> { $"recording:\"{EscapeLucene(trackTitle)}\"" };
            if (!string.IsNullOrWhiteSpace(albumTitle))
                parts.Add($"release:\"{EscapeLucene(albumTitle)}\"");
            if (!string.IsNullOrWhiteSpace(artistName))
                parts.Add($"artist:\"{EscapeLucene(artistName)}\"");
            var query = string.Join(" AND ", parts);

            var url = $"recording?query={Uri.EscapeDataString(query)}&fmt=json&limit=25";
            var callDesc = $"track=\"{trackTitle}\" album=\"{albumTitle ?? "(none)"}\" artist=\"{artistName ?? "(none)"}\"";
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

                    var artistMbid = "";
                    var creditText = "";
                    if (r.ArtistCredit != null)
                    {
                        var names = new List<string>();
                        foreach (var c in r.ArtistCredit)
                        {
                            names.Add(c.Name ?? "");
                            if (artistMbid == "" && c.Artist?.Id != null)
                                artistMbid = c.Artist.Id;
                        }
                        creditText = string.Join("", names);
                    }

                    // Added 2026-07-18: pick ONE representative release to source the
                    // richness fields from (a recording can appear on many releases with
                    // different status/type -- see MbRecordingResult doc comment for why
                    // these are richness-only, not correctness signals). Prefer the first
                    // "Official" release if one exists, since that's the one most likely to
                    // carry populated relationship data; otherwise just the first release
                    // returned. ReleaseCount is the true count of distinct releases this
                    // recording appears on (not any single release's own "count" field,
                    // which represents something else -- how many times the recording's
                    // title occurs within that one release's own tracklist).
                    var representativeRelease = r.Releases?.FirstOrDefault(rel => rel.Status == "Official") ?? r.Releases?.FirstOrDefault();

                    results.Add(new MbRecordingResult
                    {
                        RecordingId = r.Id ?? "",
                        ArtistMbid = artistMbid,
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
                    });
                }
            }

            _logger.Debug("MbApi", "  -> {0} recording result(s):", results.Count);
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                _logger.Debug("MbApi", "       Recording #{0}", i + 1);
                _logger.Debug("MbApi", "         ID:     {0}", r.RecordingId);
                _logger.Debug("MbApi", "         Track:  \"{0}\" (matches queried title: {1})", r.TrackTitle, r.TrackTitleMatches);
                _logger.Debug("MbApi", "         Artist: {0}", r.ArtistCreditText);
                _logger.Debug("MbApi", "         Album:  \"{0}\" (matches queried album: {1})", r.ReleaseTitle, r.ReleaseTitleMatches);
                _logger.Debug("MbApi", "         Length: {0}  Status: {1}  Type: {2}  Releases: {3}  Score: {4}",
                    r.LengthMs.HasValue ? $"{r.LengthMs}ms" : "(none)", r.ReleaseStatus ?? "(none)", r.ReleaseGroupPrimaryType ?? "(none)", r.ReleaseCount, r.Score);
                // NOTE: no AlbumArtist or Relationship fields here -- a raw recording
                // search result doesn't carry either. AlbumArtist isn't a MusicBrainz
                // concept at the recording level at all (that's Emby's field, not
                // MB's); relationships only exist via a SEPARATE GetRelationships call
                // on this recording's ID, logged separately when/if that call happens.
            }
            return results;
        }

        // MusicBrainz's own duration-bucketing width for the "qdur" search field --
        // INFERRED, not confirmed against MB documentation: reverse-engineered from
        // one working query (351000ms observed -> qdur:[173 TO 177] worked; 351/2 =
        // 175.5, which centers in that window). Not a config value, deliberately --
        // this isn't a lever we control, it's a guess at a fixed property of MB's
        // own index. If a future duration range shows this assumption breaking,
        // fix it here, in one place, with a name that says exactly what's being
        // assumed (2026-07-19, per direct correction from Nick on the earlier draft
        // that wrongly treated this as configurable).
        private const int AssumedMbQdurBucketSeconds = 2;

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
        public IReadOnlyList<MbRecordingResult> SearchRecordingByTitleAndDuration(string trackTitle, int observedDurationMs, int qdurToleranceBuckets)
        {
            int centerBucket = (int)Math.Round(observedDurationMs / 1000.0 / AssumedMbQdurBucketSeconds);
            int low = Math.Max(0, centerBucket - qdurToleranceBuckets);
            int high = centerBucket + qdurToleranceBuckets;

            var query = $"recording:\"{EscapeLucene(trackTitle)}\" AND qdur:[{low} TO {high}]";
            var url = $"recording?query={Uri.EscapeDataString(query)}&fmt=json&limit=100";
            var callDesc = $"track=\"{trackTitle}\" qdur=[{low} TO {high}] (observedMs={observedDurationMs})";
            var body = Get(url, "SearchRecordingByTitleAndDuration", callDesc);
            var parsed = body == null ? null : DeserializeJson<RecordingSearchResponseDto>(body);

            var results = new List<MbRecordingResult>();
            if (parsed?.Recordings != null)
            {
                foreach (var r in parsed.Recordings)
                {
                    var artistMbid = "";
                    var creditText = "";
                    if (r.ArtistCredit != null)
                    {
                        var names = new List<string>();
                        foreach (var c in r.ArtistCredit)
                        {
                            names.Add(c.Name ?? "");
                            if (artistMbid == "" && c.Artist?.Id != null)
                                artistMbid = c.Artist.Id;
                        }
                        creditText = string.Join("", names);
                    }

                    var representativeRelease = r.Releases?.FirstOrDefault(rel => rel.Status == "Official") ?? r.Releases?.FirstOrDefault();

                    results.Add(new MbRecordingResult
                    {
                        RecordingId = r.Id ?? "",
                        ArtistMbid = artistMbid,
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
                    });
                }
            }

            _logger.Debug("MbApi", "  -> {0} recording result(s) within qdur:[{1} TO {2}]:", results.Count, low, high);
            return results;
        }

        // ---- C5: relationships (work-level + recording-level, one call) ------

        public IReadOnlyList<MbRelationship> GetRelationships(string recordingId)
        {
            if (_knownRelationships.TryGetValue(recordingId, out var cached))
            {
                _logger.Debug("MbApi", "[GetRelationships] recordingId={0} -- cached from an earlier call, no live call needed. {1} relationship(s).", recordingId, cached.Count);
                return cached;
            }

            var url = $"recording/{recordingId}?inc=work-rels+artist-rels+work-level-rels&fmt=json";
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

            _logger.Debug("MbApi", "  -> {0} relationship(s):", results.Count);
            foreach (var r in results)
                _logger.Debug("MbApi", "       {0}({1})={2}", r.RelationshipType, r.Level, r.ArtistMbid);
            _knownRelationships[recordingId] = results;
            return results;
        }

        // ---- Phase 1 additions: display name / aliases by MBID ---------------

        public string GetArtistDisplayName(string artistMbid)
        {
            if (_knownArtistNames.TryGetValue(artistMbid, out var cached))
            {
                _logger.Debug("MbApi", "[GetArtistDisplayName] artistMbid={0} -- cached from an earlier SearchArtist result, no live call needed. name=\"{1}\"", artistMbid, cached);
                return cached;
            }

            var url = $"artist/{artistMbid}?fmt=json";
            var body = Get(url, "GetArtistDisplayName", $"artistMbid={artistMbid}");
            var parsed = body == null ? null : DeserializeJson<ArtistDto>(body);
            var name = parsed?.Name ?? "";
            _logger.Debug("MbApi", "  -> name=\"{0}\"", name);
            if (name != "") _knownArtistNames[artistMbid] = name;
            return name;
        }

        public IReadOnlyList<string> GetArtistAliases(string artistMbid)
        {
            if (_knownArtistAliases.TryGetValue(artistMbid, out var cached))
            {
                _logger.Debug("MbApi", "[GetArtistAliases] artistMbid={0} -- cached from an earlier SearchArtist result, no live call needed. {1} alias(es).", artistMbid, cached.Count);
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

            _logger.Debug("MbApi", "  -> {0} alias(es): {1}", aliases.Count, string.Join(", ", aliases));
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
                _logger.Debug("MbApi", "[GetArtistRelationships] artistMbid={0} -- cached from an earlier call, no live call needed. {1} relation(s).", artistMbid, cached.Count);
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

            _logger.Debug("MbApi", "  -> {0} artist relation(s):", results.Count);
            foreach (var r in results)
                _logger.Debug("MbApi", "       type=\"{0}\" ({1}) -> \"{2}\" [{3}]", r.RelationshipType, r.RelationshipTypeId, r.ArtistName, r.ArtistMbid);
            _knownArtistRelationships[artistMbid] = results;
            return results;
        }

        // ---- shared plumbing ---------------------------------------------------

        private string? Get(string relativeUrl, string callName, string callDescription)
        {
            TotalApiCalls++;
            ApiCallsByType[callName] = ApiCallsByType.TryGetValue(callName, out var n) ? n + 1 : 1;
            _logger.Info("MbApi", "[{0}] {1}", callName, callDescription);
            _logger.Info("MbApi", "  GET {0}{1}", BaseUrl, relativeUrl);
            try
            {
                var response = _http.GetAsync(relativeUrl).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    _logger.Warn("MbApi", "  -> HTTP {0} for {1}", (int)response.StatusCode, relativeUrl);
                    return null;
                }
                return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.ErrorException("MbApi", $"  -> request failed for {relativeUrl}", ex);
                return null;
            }
        }

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

        private static string EscapeLucene(string value)
            => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

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
        }

        [DataContract]
        private class ArtistCreditDto
        {
            [DataMember(Name = "name")] public string? Name { get; set; }
            [DataMember(Name = "artist")] public ArtistRefDto? Artist { get; set; }
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
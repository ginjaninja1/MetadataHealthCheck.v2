namespace MetadataHealthCheck.v2.Resolvers.MusicBrainz.Client
{
    public class MbArtistResult
    {
        public string Mbid { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Disambiguation { get; set; }
        public int Score { get; set; }               // MB's own text-relevance score — pool-admission filter ONLY, §5.4

        // Added 2026-07-12 (coding checklist item 1): real ws/2/artist search responses
        // return registered aliases inline on each result, no extra inc= parameter
        // needed (confirmed against real MusicBrainz data). Consumed by
        // SoftBucketStrategy's Stage 1 admission gate (§5.3) as of 2026-07-13.
        public List<string> Aliases { get; set; } = new();
    }

    public class MbAlbumTitle
    {
        public string Title { get; set; } = "";
        public bool IsDistinctive { get; set; } = true;  // false for "Greatest Hits"/self-titled-style generic titles, §6.1
    }

    public class MbRecordingResult
    {
        public string RecordingId { get; set; } = "";
        public string ArtistMbid { get; set; } = "";
        public string TrackTitle { get; set; } = "";
        public string ReleaseTitle { get; set; } = "";
        public bool TrackTitleMatches { get; set; }
        public bool ReleaseTitleMatches { get; set; }

        // Added 2026-07-13: the literal artist-credit text MB returned for this
        // recording — distinct from ArtistMbid (already-resolved identity). Needed so
        // RecordingLookup can determine whether a hit matched the candidate's primary
        // name or only a registered alias (MatchedViaAlias, §5.4/§6.3), and whether the
        // match is trustworthy at all (NameDistanceEvidenceCollector.EvaluateRecordingMatch).
        public string ArtistCreditText { get; set; } = "";
    }

    // Relationship level per §7.2 C5: work-level (writer/composer/lyricist/librettist,
    // from work-rels+work-level-rels) vs recording-level (producer/arranger, from
    // artist-rels). Both come back from ONE call per §5.4/§7.2's own description ("see
    // §5.4 for why both inc terms are required in one call") — added 2026-07-13,
    // replacing the work-only MbWorkRelationship/GetWorkRelationships that predated
    // reading the spec's actual C5 description closely.
    public enum RelationshipLevel
    {
        Work,
        Recording,
    }

    public class MbRelationship
    {
        public string RelationshipType { get; set; } = "";   // writer|composer|lyricist|librettist|producer|arranger|...
        public string ArtistMbid { get; set; } = "";
        public RelationshipLevel Level { get; set; }
    }

    // Added 2026-07-18: artist-to-artist relationships (e.g. "is person" linking a
    // stage name to a real-person identity), from a NEW artist-rels call distinct
    // from GetRelationships (which is recording-scoped). No Direction field: verified
    // 2026-07-18 against a real two-artist round trip (Del Serino <-> Cirino
    // Colacrai) that the same relationship type-id appears from either artist's own
    // artist-rels fetch, just with "direction" flipped depending on which side was
    // queried -- so direction carries no extraction-relevant information and callers
    // should just treat ArtistMbid as "the other artist in this relation", full stop.
    public class MbArtistRelationship
    {
        public string ArtistMbid { get; set; } = "";      // the OTHER artist in the relation
        public string ArtistName { get; set; } = "";      // for logging only
        public string RelationshipType { get; set; } = ""; // e.g. "is person"
        public string RelationshipTypeId { get; set; } = ""; // MB's stable type-id GUID, e.g. "dd9886f2-..."
    }

    /// <summary>
    /// Abstraction over §7.2's call catalog (C1-C6). Live implementation would hit
    /// musicbrainz.org, which is unreachable from this build sandbox (not on the
    /// allowed-domains list) — this is expected for a plugin engine and is not a
    /// build blocker, per §17.1's own fixture-based testing strategy.
    /// </summary>
    public interface IMusicBrainzApiClient
    {
        IReadOnlyList<MbArtistResult> SearchArtist(string name);                                  // C1
        IReadOnlyList<MbAlbumTitle> GetReleaseGroupTitles(string artistMbid);                       // C2 (subset)

        // Extended 2026-07-12 with an artistName parameter (nullable, defaults to null so
        // pre-existing call sites don't need every caller touched at once) to support
        // RecordingLookup.cs's three-rung fallback ladder: track+artist+album -> track+album
        // -> track alone.
        IReadOnlyList<MbRecordingResult> SearchRecording(string trackTitle, string? albumTitle, string? artistName = null);     // C3/C4

        // Renamed from GetWorkRelationships 2026-07-13: this single call now yields
        // BOTH work-level and recording-level relations (RelationshipLevel discriminates),
        // per §7.2 C5's actual description — not two separate calls.
        IReadOnlyList<MbRelationship> GetRelationships(string recordingId);                        // C5

        // Phase 1 addition, not literally in §7.2's catalog: NameDistanceEvidenceCollector
        // needs the candidate's MB display name to compare against the source name. In the
        // full build this rides along on C2's artist lookup; pulled out separately here
        // since C2's other fields (aliases/tags/url-rels) aren't implemented until Phase 2.
        string GetArtistDisplayName(string artistMbid);

        // Added 2026-07-13, same rationale as GetArtistDisplayName above: RecordingLookup
        // needs a candidate's registered aliases (by MBID) to determine MatchedViaAlias
        // on a recording hit, without re-issuing the original SearchArtist(name) call that
        // produced this candidate in the first place.
        IReadOnlyList<string> GetArtistAliases(string artistMbid);

        // Added 2026-07-18: artist-to-artist relationships (e.g. "is person"), used by
        // the artist candidate generator to populate Candidate.RelationshipMbids so
        // composer-only/performs-as identities can be found via recording-relationship
        // evidence later. Distinct from GetRelationships (C5), which is scoped to a
        // recording, not an artist.
        IReadOnlyList<MbArtistRelationship> GetArtistRelationships(string artistMbid);
    }
}
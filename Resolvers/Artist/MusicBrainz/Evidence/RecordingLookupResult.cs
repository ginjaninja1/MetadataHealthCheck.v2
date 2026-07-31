using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Client.Model;

namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Evidence
{
    public class RecordingLookupResult
    {
        public MbRecordingResult? Recording { get; set; }
        public RecordingLookupRung RungReached { get; set; } = RecordingLookupRung.NotFound;

        // Confirmation is by MBID equality throughout, which has no "primary
        // name vs. alias" distinction to report -- always false. Retained
        // because ArtistMusicBrainzConfig.NameMatchWeight/AliasMatchWeight
        // still reference it at scoring time.
        public bool MatchedViaAlias { get; set; }

        // True when this recording was confirmed via the relationship-scan
        // path (the candidate's MBID or one of its RelationshipMbids found in
        // the recording's own relationship data) rather than via
        // performer-identity (an artist-credit MBID match).
        public bool ConfirmedViaRelationship { get; set; }

        // The specific relationship entry that confirmed the candidate, when
        // ConfirmedViaRelationship is true (null otherwise) -- carried here so
        // callers don't need a second GetRelationships call/scan to find out
        // what already confirmed the candidate inside the confirmation walk.
        public MbRelationship? ConfirmingRelationship { get; set; }
    }
}

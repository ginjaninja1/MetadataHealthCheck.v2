using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Evidence
{
    // This resolver's extra evidence detail, on top of the generic
    // EvidenceRecord fields -- these only ever mean something for the
    // Artist/MusicBrainz resolver (track/album/role/relationship/rung
    // concepts don't apply to every resolver's domain), so they live here
    // rather than on the shared base type.
    public class MusicBrainzEvidenceRecord : EvidenceRecord
    {
        public string? Role { get; set; }
        public string? SourceTrackId { get; set; }
        public string? AlbumId { get; set; }
        public string? RelationshipType { get; set; }

        // The specific rung a corroboration hit was confirmed at, if evidence type is rung-based.
        public string? Rung { get; set; }
    }
}
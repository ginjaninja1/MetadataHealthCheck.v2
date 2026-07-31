namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Client.Model
{
    // Work-level (writer/composer/lyricist/librettist, from work-rels+work-level-rels)
    // vs recording-level (producer/arranger, from artist-rels). Both come back
    // from one call (see IMusicBrainzApiClient.GetRelationships).
    public enum RelationshipLevel
    {
        Work,
        Recording,
    }
}

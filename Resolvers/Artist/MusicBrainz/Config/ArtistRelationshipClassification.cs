namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Config
{
    /// <summary>
    /// Identity is the only classification the candidate identity-fold pass is
    /// allowed to act on; everything else is GroupMembership until a genuinely
    /// distinct third kind of relationship shows up.
    /// </summary>
    public enum ArtistRelationshipClassification
    {
        Identity,
        GroupMembership,
    }
}

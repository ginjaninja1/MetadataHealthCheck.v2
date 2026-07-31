namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.CandidateGeneration
{
    // Name=0 (exact normalized name match) outranks Alias=1 (exact normalized
    // alias match) outranks Neither=2 (admitted on MB score alone). Lower value
    // = higher tier.
    internal enum ArtistMatchTier
    {
        Name = 0,
        Alias = 1,
        Neither = 2,
    }
}

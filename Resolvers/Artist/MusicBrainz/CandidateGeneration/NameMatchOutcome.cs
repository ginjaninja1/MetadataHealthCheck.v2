namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.CandidateGeneration
{
    internal enum NameMatchOutcome
    {
        MatchedViaName,
        MatchedViaAlias,
        TooPoorToTrust,
    }
}

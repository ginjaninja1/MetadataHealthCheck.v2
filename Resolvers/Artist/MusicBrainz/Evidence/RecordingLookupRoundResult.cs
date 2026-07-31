namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Evidence
{
    /// <summary>
    /// The result of one round within the multi-candidate confirmation ladder,
    /// where a round is either (a) a cheap, no-API-call performer-credit check
    /// against every survivor at a rung, run once for all still-pending
    /// candidates, or (b) one recording's GetRelationships fetch, checked
    /// against all still-pending candidates at once. NewlyConfirmed contains
    /// only candidates that confirmed in this round -- never a running total;
    /// the caller accumulates.
    /// </summary>
    public class RecordingLookupRoundResult
    {
        public IReadOnlyDictionary<string, RecordingLookupResult> NewlyConfirmed { get; set; } = new Dictionary<string, RecordingLookupResult>();
        public string RoundDescription { get; set; } = "";
    }
}

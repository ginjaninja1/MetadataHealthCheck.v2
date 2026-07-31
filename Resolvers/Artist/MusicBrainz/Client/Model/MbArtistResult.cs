namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Client.Model
{
    public class MbArtistResult
    {
        public string Mbid { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Disambiguation { get; set; }

        // MusicBrainz's own text-relevance score (0-100) -- a pool-admission
        // filter only, never a correctness signal on its own.
        public int Score { get; set; }

        // MusicBrainz's own artist "type" (e.g. "Person", "Group"), returned
        // inline on every artist search hit. Used only by pathway-local bucket
        // filters; never a candidate-admission gate.
        public string Type { get; set; } = "";

        public List<string> Aliases { get; set; } = new();
    }
}

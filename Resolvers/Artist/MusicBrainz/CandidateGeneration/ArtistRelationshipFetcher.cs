using MetadataHealthCheck.v2.Diagnostics;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Client;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Config;

namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.CandidateGeneration
{
    /// <summary>
    /// Fetches and filters a candidate's artist-relationships. Called eagerly,
    /// once per admitted candidate, rather than lazily in sampler order -- a
    /// deliberate trade of some up-front API calls for having full relationship
    /// data available before the identity-fold pass needs to reason about it.
    /// </summary>
    internal class ArtistRelationshipFetcher
    {
        private readonly IMusicBrainzApiClient _client;
        private readonly StructuredLogger? _logger;

        public ArtistRelationshipFetcher(IMusicBrainzApiClient client, StructuredLogger? logger)
        {
            _client = client;
            _logger = logger;
        }

        public IReadOnlyList<AdmittedArtistRelationship> FetchValid(string candidateMbid, string candidateName, CandidateGenerationConfig cgConfig)
        {
            var relations = _client.GetArtistRelationships(candidateMbid);
            if (relations.Count == 0)
                return Array.Empty<AdmittedArtistRelationship>();

            _logger?.Info("ArtistCandidateGen", "  [{0}] \"{1}\" -- fetching artist-rels...", candidateMbid, candidateName);

            var validTypes = cgConfig.ValidArtistRelationshipTypes
                .GroupBy(t => t.TypeId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Classification, StringComparer.OrdinalIgnoreCase);

            var admitted = new List<AdmittedArtistRelationship>();
            foreach (var rel in relations)
            {
                if (validTypes.TryGetValue(rel.RelationshipTypeId, out var classification))
                {
                    _logger?.Info("ArtistCandidateGen", "    relation type=\"{0}\" -> \"{1}\" [{2}] -- ADMITTED as {3}.", rel.RelationshipType, rel.ArtistName, rel.ArtistMbid, classification);
                    admitted.Add(new AdmittedArtistRelationship { Name = rel.ArtistName, Mbid = rel.ArtistMbid, Classification = classification });
                }
                else
                {
                    _logger?.Info("ArtistCandidateGen", "    relation type=\"{0}\" -> \"{1}\" [{2}] -- DROPPED: not a valid relationship type-id.", rel.RelationshipType, rel.ArtistName, rel.ArtistMbid);
                }
            }
            return admitted;
        }
    }
}

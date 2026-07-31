using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Core.Interfaces
{
    public interface IMatchRepository
    {
        void SaveCandidate(Candidate candidate);
        void SaveEvidence(EvidenceRecord evidence);
        void SaveMatchResult(MatchResult result);
        MatchResult? GetExisting(string sourceSystem, string sourceId, string targetSystem);
    }
}

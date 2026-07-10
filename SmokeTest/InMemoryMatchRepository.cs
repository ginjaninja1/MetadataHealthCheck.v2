using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;

namespace SmokeTest;

// Bypasses the real SQLite-backed MatchRepository entirely. This exists
// because SQLitePCL.pretty.core 1.2.2 (the real package, matching the
// reference plugin Emby.AutoOrganize) has a narrow, undocumented binary
// compatibility window against SQLitePCLRaw provider versions - inside a
// real Emby host this is a non-issue (the host supplies an already-
// initialized provider before any plugin code runs), but a standalone
// console harness has to supply and version-match one itself, which turned
// into an unproductive rabbit hole. This fake sidesteps that question
// entirely so the actual thing worth verifying here - candidate generation,
// evidence collection, scoring, decision gate, identity cache behavior -
// can be tested without any native SQLite dependency at all. Real SQLite
// persistence should be verified separately, once, inside an actual Emby
// host where the provider-version question doesn't exist.
public class InMemoryMatchRepository : IMatchRepository
{
    private readonly List<Candidate> _candidates = new();
    private readonly List<EvidenceRecord> _evidence = new();
    private readonly List<MatchResult> _results = new();

    public void SaveCandidate(Candidate candidate) => _candidates.Add(candidate);
    public void SaveEvidence(EvidenceRecord evidence) => _evidence.Add(evidence);
    public void SaveMatchResult(MatchResult result) => _results.Add(result);

    public MatchResult? GetExisting(string sourceSystem, string sourceId, string targetSystem)
    {
        return _results
            .Where(r => r.SourceSystem == sourceSystem && r.SourceId == sourceId && r.TargetSystem == targetSystem)
            .OrderByDescending(r => r.DecidedAt)
            .FirstOrDefault();
    }
}
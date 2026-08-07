using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;

namespace SmokeTest;

// Bypasses the real SQLite-backed MatchRepository entirely. This exists
// because the public SQLitePCL.pretty.core NuGet package (which this repo
// used before discovering the reference plugins never touch it) has named-
// parameter binds that call a public SQLitePCLRaw method which simply
// doesn't exist at ANY public version -- confirmed empirically, not
// assumed: a newer public version was missing one overload, an older one
// was missing a different overload. Emby.AutoOrganize/Emby.Sqlite avoid this
// entirely by referencing Emby's own private SQLitePCL.pretty.dll, whose
// named-bind implementation never calls that method at all (see
// Storage/Sqlite/SqliteExtensions.cs and the real MetadataHealthCheck.v2.csproj
// for the actual fix now in place). This in-memory fake predates that fix and
// remains useful for its original purpose regardless: exercising candidate
// generation, evidence collection, scoring, decision gate, and identity cache
// behavior without any native SQLite dependency at all, standalone from
// whatever the SQLite layer is doing.
public class InMemoryMatchRepository : IMatchRepository
{
    private readonly List<Candidate> _candidates = new();
    private readonly List<EvidenceRecord> _evidence = new();
    private readonly List<MatchResult> _results = new();

    // Exposed 2026-07-16 so SmokeTest/Program.cs can build a post-run, per-artist
    // evidence/score summary -- everything ResolveOne generated for this artist is
    // already sitting here (fresh repo instance per artist), no engine changes
    // needed to surface it.
    public IReadOnlyList<Candidate> Candidates => _candidates;
    public IReadOnlyList<EvidenceRecord> Evidence => _evidence;

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
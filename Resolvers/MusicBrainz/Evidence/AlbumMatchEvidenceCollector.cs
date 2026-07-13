using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Resolvers.MusicBrainz.Client;
using MetadataHealthCheck.v2.Sources.Emby;

namespace MetadataHealthCheck.v2.Resolvers.MusicBrainz.Evidence
{
    /// <summary>
    /// Album-Match precursor (§5.2): once per candidate, fetch the candidate's MB
    /// release-group titles (C2) and string-match against Emby's known album list for
    /// the source artist. Distinctive titles score higher than generic ones (§6.1).
    /// This is the first observation fed into the Sequential Sampler for any given
    /// candidate — it runs once, before any per-track observation exists, which is
    /// exactly why the §5.2 supersession rule (a later Tier 1 corroboration for the
    /// SAME album should replace this, not stack with it) has to live in the scorer:
    /// this collector has no way to know in advance whether that will happen.
    ///
    /// KNOWN LIMITATION: IEvidenceCollector.Collect returns at most one EvidenceRecord
    /// per candidate (§11.2's interface shape, not a Phase 2 gap) — so if a
    /// candidate's real MB discography and the source's real Emby library share
    /// multiple matching albums, only the first one found is evidenced here. Revisit
    /// if this proves to matter once real libraries are tested; not solved in this
    /// pass.
    /// </summary>
    public class AlbumMatchEvidenceCollector : IEvidenceCollector<EmbyArtist>
    {
        private readonly IMusicBrainzApiClient _client;

        public AlbumMatchEvidenceCollector(IMusicBrainzApiClient client) => _client = client;

        public string EvidenceType => "AlbumMatch";

        public EvidenceRecord? Collect(EmbyArtist source, Candidate candidate, ResolutionContext context)
        {
            var mbAlbums = _client.GetReleaseGroupTitles(candidate.TargetId);
            if (mbAlbums.Count == 0) return null;

            foreach (var track in source.Tracks)
            {
                var match = mbAlbums.FirstOrDefault(a => a.Title.Equals(track.AlbumName, StringComparison.OrdinalIgnoreCase));
                if (match == null) continue;

                string evidenceType = match.IsDistinctive ? "AlbumMatch.Distinctive" : "AlbumMatch.Generic";
                return new EvidenceRecord
                {
                    CandidateId = candidate.Id,
                    EvidenceType = evidenceType,
                    RawValue = match.Title,
                    Role = null, // static, candidate-pair-level evidence — not tied to a specific track/role
                    AlbumId = track.AlbumId, // load-bearing: the §5.2 supersession rule matches on this
                    Rationale = $"MusicBrainz lists \"{match.Title}\" as a release by this candidate, matching Emby's \"{track.AlbumName}\".",
                };
            }

            return null;
        }
    }
}
using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Sources.Emby;

namespace MetadataHealthCheck.v2.Resolvers.MusicBrainz.Evidence
{
    /// <summary>
    /// Corroboration Tier evidence (§6.1/§6.3): how many of {artist, track, album}
    /// a recording-lookup hit actually confirms.
    ///
    ///   Tier 1 (full triple, +3.5): track title AND release title both match.
    ///   Tier 2 (artist+track, +1.8): track title matches, release title doesn't
    ///     (or there was no album to check).
    ///   Tier 3 (single field, +0.5): neither track nor release title matched —
    ///     the recording only confirms this candidate's artist identity at all
    ///     (RecordingLookup already filters by ArtistMbid==candidate, so an artist
    ///     match is implicit in getting a hit at all).
    ///
    /// Artist identity itself is never in question here — that's what
    /// RecordingLookup's own trustworthiness check (§5.4) already gated on before
    /// returning a hit at all. This collector's RawValue records TrackTitleMatches/
    /// ReleaseTitleMatches/RungReached as observed facts; the tier->LLR mapping and
    /// the NameMatchWeight/AliasMatchWeight multiplier (from MatchedViaAlias) are
    /// both applied at scoring time (§5.4, never pre-baked here).
    /// </summary>
    public class CorroborationTierEvidenceCollector : IObservationEvidenceCollector<EmbyArtist>
    {
        private readonly RecordingLookup _recordingLookup;

        public CorroborationTierEvidenceCollector(RecordingLookup recordingLookup) => _recordingLookup = recordingLookup;

        public string EvidenceType => "CorroborationTier";

        public EvidenceRecord? Collect(EmbyArtist source, Candidate candidate, IObservationUnit unit, ResolutionContext context)
        {
            if (unit is not EmbyTrackObservationUnit trackUnit) return null;
            var track = trackUnit.Track;

            // Composer-tier: relationship-scan path -- see WorkRelationshipEvidenceCollector.
            RecordingLookupResult lookup;
            if (string.Equals(track.Role, "Composer", StringComparison.OrdinalIgnoreCase))
            {
                var coCredits = track.AlbumArtists.Concat(track.Artists).Select(c => c.Name);
                lookup = _recordingLookup.LookupComposerTier(candidate.TargetId, track, coCredits);
            }
            else
            {
                lookup = _recordingLookup.Lookup(candidate.TargetId, track, artistName: source.DisplayName);
            }
            var rec = lookup.Recording;
            if (rec == null) return null;

            string tier = (rec.TrackTitleMatches, rec.ReleaseTitleMatches) switch
            {
                (true, true) => "CorroborationTier.Tier1",
                (true, false) => "CorroborationTier.Tier2",
                _ => "CorroborationTier.Tier3",
            };

            return new EvidenceRecord
            {
                CandidateId = candidate.Id,
                EvidenceType = tier,
                RawValue = $"track={rec.TrackTitleMatches} album={rec.ReleaseTitleMatches} rung={lookup.RungReached}",
                Role = track.Role,
                SourceTrackId = track.TrackId,
                AlbumId = track.AlbumId,
                MatchedViaAlias = lookup.MatchedViaAlias,
                Rationale = BuildRationale(tier, track, lookup),
            };
        }

        private static string BuildRationale(string tier, EmbyTrackCredit track, RecordingLookupResult lookup)
        {
            string tierDescription = tier switch
            {
                "CorroborationTier.Tier1" => "full-triple (artist+track+album)",
                "CorroborationTier.Tier2" => "artist+track, no album",
                _ => "single-field (artist only)",
            };
            string aliasNote = lookup.MatchedViaAlias ? " (matched via a registered alias)" : "";
            return $"MusicBrainz {tierDescription} corroboration for \"{track.TrackName}\"{aliasNote}.";
        }
    }
}
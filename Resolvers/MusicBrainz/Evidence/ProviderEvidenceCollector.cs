using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Sources.Emby;

namespace MetadataHealthCheck.v2.Resolvers.MusicBrainz.Evidence
{
    /// <summary>
    /// Tier 0 evidence (§6.1: "ProviderIds asserted MBID, confirmed", +5.0) --
    /// built 2026-07-15. Previously spec'd but entirely absent from code: no
    /// collector read EmbyTrackCredit.ProviderIds and no EvidenceWeights entry
    /// existed beyond a doc comment reference.
    ///
    /// This is NOT the RecordingLookup ladder and doesn't call MusicBrainz at all --
    /// it's about whether the TRACK's own file tags (Emby's E4 field, §8.2) already
    /// assert a MusicBrainz identity, because something (prior tagging, MusicBrainz
    /// Picard, etc.) already wrote it there. Per §6.1 this is scored evidence like
    /// anything else -- it does not bypass the sampler or the decision gate, it's
    /// just high-weight enough that it usually resolves things in one observation.
    ///
    /// INTERPRETATION CHOICE, flagged rather than silently assumed: neither §6.1 nor
    /// §8.2 specify the literal ProviderIds dictionary key shape. This collector
    /// reads the key "MusicBrainzArtist" -- a tag directly asserting the artist's own
    /// MBID, which is the most direct and unambiguous reading of "already confirmed
    /// via prior tagging" for the ARTIST resolution case this plugin handles. A real
    /// Emby install more commonly tags "MusicBrainzArtistId" (Emby/Kodi/Picard's own
    /// convention) or a recording-level id requiring a further lookup to resolve to
    /// an artist -- worth confirming against a real Emby host's actual tag naming
    /// before this ships, not assumed correct here.
    /// </summary>
    public class ProviderIdEvidenceCollector : IObservationEvidenceCollector<EmbyArtist>
    {
        public const string ProviderIdKey = "MusicBrainzArtist";

        public string EvidenceType => "ProviderIds";

        public IEnumerable<EvidenceRecord> Collect(EmbyArtist source, Candidate candidate, IObservationUnit unit, ResolutionContext context)
        {
            if (unit is not EmbyTrackObservationUnit trackUnit) yield break;
            var track = trackUnit.Track;

            if (!track.ProviderIds.TryGetValue(ProviderIdKey, out var taggedMbid)) yield break;
            if (!string.Equals(taggedMbid, candidate.TargetId, StringComparison.OrdinalIgnoreCase)) yield break;

            yield return new EvidenceRecord
            {
                CandidateId = candidate.Id,
                EvidenceType = "ProviderIds.Confirmed",
                RawValue = taggedMbid,
                Role = track.Role,
                SourceTrackId = track.TrackId,
                AlbumId = track.AlbumId,
                // Contributing=false (2026-07-17 settled directive): only CorroborationTier
                // evidence (RecordingCorroborationEvidenceCollector) is allowed to affect
                // the decision right now. Still logged/computed since it's cheap (file tags
                // already on the track, no API call) and may prove useful once composer/
                // anchor-strategy work resumes -- just not scoring today.
                Contributing = false,
                Rationale = $"Emby's own file tags on \"{track.TrackName}\" already assert MusicBrainz artist id {taggedMbid} for this candidate (Tier 0).",
            };
        }
    }
}
using MetadataHealthCheck.v2.Core.Interfaces;

namespace MetadataHealthCheck.v2.Sources.Emby
{
    /// <summary>
    /// One track-credit, bucketed by the artist's role on that track
    /// (AlbumArtist/Artist/Composer). Core's SequentialSampler only ever sees
    /// this through the IObservationUnit interface -- it has no idea a "track"
    /// is involved.
    /// </summary>
    public class EmbyTrackObservationUnit : IObservationUnit
    {
        public EmbyTrackCredit Track { get; }

        public EmbyTrackObservationUnit(EmbyTrackCredit track) => Track = track;

        public string BucketKey => Track.Role;

        public string Describe()
        {
            var providerIds = Track.ProviderIds.Count > 0
                ? " ProviderIds: " + string.Join(", ", Track.ProviderIds.Select(p => $"{p.Key}={p.Value}"))
                : "";
            return $"\"{Track.TrackName}\" on \"{Track.AlbumName}\"  [{Track.Role}]{providerIds}";
        }
    }
}

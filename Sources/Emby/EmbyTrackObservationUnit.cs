using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Sources.Emby
{
    /// <summary>
    /// The Artist/MusicBrainz case's concrete IObservationUnit: one track-credit,
    /// bucketed by the artist's role on that track (AlbumArtist/Artist/Composer).
    /// SequentialSampler (Core/Engine) only ever sees this through the IObservationUnit
    /// interface -- it has no idea a "track" is involved.
    /// </summary>
    public class EmbyTrackObservationUnit : IObservationUnit
    {
        public EmbyTrackCredit Track { get; }

        public EmbyTrackObservationUnit(EmbyTrackCredit track) => Track = track;

        public string BucketKey => Track.Role;
    }
}
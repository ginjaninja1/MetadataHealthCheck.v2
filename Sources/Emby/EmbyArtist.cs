using MetadataHealthCheck.v2.Core.Interfaces;

namespace MetadataHealthCheck.v2.Sources.Emby
{
    public class EmbyArtist : ISourceEntity
    {
        public string SourceSystem => "Emby";
        public string EntityType => "Artist";
        public string SourceId { get; set; } = "";
        public string DisplayName { get; set; } = "";

        // The tracks this artist is credited on, carrying role/album/duration/ProviderIds.
        public List<EmbyTrackCredit> Tracks { get; set; } = new();
    }
}

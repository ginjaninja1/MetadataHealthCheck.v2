using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Sources.Emby
{
    public class EmbyArtistProvider : ISourceEntityProvider<EmbyArtist>
    {
        private readonly IEmbyLibraryReader _reader;

        // Comma-separated; each token tried as a Guid first, falling back to
        // case-insensitive name match; empty = no restriction. Mirrors
        // DeveloperConfig.ArtistFilter (§10.2) — full DeveloperConfig object
        // arrives in Phase 5 alongside the config pages; this constructor
        // param is the Phase 1 stand-in for that one field.
        private readonly string? _artistFilter;

        public EmbyArtistProvider(IEmbyLibraryReader reader, string? artistFilter = null)
        {
            _reader = reader;
            _artistFilter = artistFilter;
        }

        public IEnumerable<EmbyArtist> GetAll(ResolutionContext context)
        {
            var all = _reader.ReadAllArtists();

            if (string.IsNullOrWhiteSpace(_artistFilter))
                return all;

            var tokens = _artistFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return all.Where(a => tokens.Any(t =>
                (Guid.TryParse(t, out _) && string.Equals(a.SourceId, t, StringComparison.OrdinalIgnoreCase))
                || string.Equals(a.DisplayName, t, StringComparison.OrdinalIgnoreCase)));
        }
    }
}

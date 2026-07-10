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

            // StringSplitOptions.TrimEntries requires netstandard2.1+/.NET Core 3+
            // and isn't available under this project's netstandard2.0 target - trim
            // manually instead. Also using the char[]-array Split overload rather
            // than the single-char one, since that convenience overload isn't
            // guaranteed present in netstandard2.0's own API surface either.
            var tokens = _artistFilter
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .ToArray();
            return all.Where(a => tokens.Any(t =>
                (Guid.TryParse(t, out _) && string.Equals(a.SourceId, t, StringComparison.OrdinalIgnoreCase))
                || string.Equals(a.DisplayName, t, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
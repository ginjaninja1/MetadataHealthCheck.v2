using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Sources.Emby
{
    public class EmbyArtistProvider : ISourceEntityProvider<EmbyArtist>
    {
        private readonly IEmbyLibraryReader _reader;

        // Comma-separated; each token tried as a Guid first, falling back to a
        // case-insensitive name match; null/empty means no restriction.
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

            // netstandard2.0 has neither StringSplitOptions.TrimEntries nor the
            // single-char Split convenience overload -- trim manually and use
            // the char[]-array overload instead.
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

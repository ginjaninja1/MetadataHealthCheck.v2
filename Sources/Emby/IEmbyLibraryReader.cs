namespace MetadataHealthCheck.v2.Sources.Emby
{
    /// <summary>
    /// Abstraction over Emby's library query (a single recursive Audio-item
    /// query, grouped in-memory by artist). The production implementation
    /// wraps Emby's ILibraryManager; TextFileEmbyLibraryReader is the
    /// text-file-backed implementation used for smoke testing.
    /// </summary>
    public interface IEmbyLibraryReader
    {
        IReadOnlyList<EmbyArtist> ReadAllArtists();
    }
}

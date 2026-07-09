namespace MetadataHealthCheck.v2.Sources.Emby
{
    /// <summary>
    /// Abstraction over §8.2's E2 call (single recursive Audio-item query, run once
    /// per sync, grouped in-memory by artist afterward). The real implementation
    /// (EmbyLibraryReader.cs, wrapping ILibraryManager) requires the actual Emby
    /// server SDK assemblies, which are not obtainable in this build sandbox
    /// (not on any allowed package registry — nuget.org is blocked here, and the
    /// SDK is normally supplied by an Emby server install, not a package feed).
    /// This is tracked as an open item in the Project Log rather than silently
    /// assumed away. FixtureEmbyLibraryReader below is the Phase-1 stand-in,
    /// consistent with §17.1's fixture-based testing philosophy.
    /// </summary>
    public interface IEmbyLibraryReader
    {
        IReadOnlyList<EmbyArtist> ReadAllArtists();
    }
}

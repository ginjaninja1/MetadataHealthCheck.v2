using MetadataHealthCheck.v2.Sources.Emby;

namespace MetadataHealthCheck.v2.Fixtures
{
    /// <summary>
    /// Replaces FixtureEmbyLibraryReader.cs (removed 2026-07-15, Project Log
    /// Directives): that class hardcoded one artist's track data directly as C#
    /// object literals, the exact "sample data welded into code" problem flagged
    /// for the whole Emby side of the fixtures (SmokeTest/Program.cs had the same
    /// problem, worse, mixed with assertions -- see that file's own rewrite).
    ///
    /// This reads a plain-text observation file instead. The MusicBrainz side
    /// (FixtureMusicBrainzApiClient.cs) is untouched and remains the model this
    /// followed: real data, kept as data, separate from the client abstraction it
    /// backs. The engine, the MusicBrainz client, and the sample data are now three
    /// separate things on both sides, not two.
    ///
    /// FILE FORMAT (track-first, per settled design discussion):
    ///
    ///   # comments start with #, blank lines separate blocks
    ///
    ///   ARTIST &lt;sourceId&gt; "&lt;DisplayName&gt;"
    ///
    ///   TRACK &lt;trackId&gt; "&lt;TrackName&gt;"
    ///   ALBUM &lt;albumId&gt; "&lt;AlbumName&gt;"
    ///   DURATION_MS &lt;int&gt;                                  # optional
    ///   PROVIDERIDS &lt;key&gt;=&lt;value&gt;[,&lt;key&gt;=&lt;value&gt;...]     # optional, Tier 0 -- this TRACK's own tags
    ///   ALBUMARTIST "&lt;Name&gt;" [mbid=&lt;mbid&gt;]                 # zero or more of each role line
    ///   ARTIST "&lt;Name&gt;" [mbid=&lt;mbid&gt;]
    ///   COMPOSER "&lt;Name&gt;" [mbid=&lt;mbid&gt;]
    ///
    ///   (repeat TRACK blocks, then blank line, then next ARTIST block)
    ///
    /// A TRACK block declares ALL real credits on that physical track -- not just
    /// the artist currently under review -- matching real Emby E2 query shape
    /// (§8.2) and the settled design decision that observation units must carry
    /// full track context, not a siloed per-artist view. EmbyTrackCredit.Role (the
    /// artist-under-review's own tier on this track) is derived automatically: for
    /// the enclosing ARTIST block's DisplayName, this loader emits one
    /// EmbyTrackCredit per role-list (AlbumArtist/Artist/Composer) that contains a
    /// name matching that DisplayName (case-insensitive) -- usually one, but
    /// nothing stops a real track crediting the same person in two roles, which
    /// would legitimately produce two observation units for that track.
    ///
    /// Disambiguating the ARTIST keyword: top-level "ARTIST &lt;id&gt; &quot;name&quot;"
    /// (two tokens then a quoted string) only appears outside a TRACK block; the
    /// per-track credit line "ARTIST &quot;name&quot;" (quoted string immediately after
    /// the keyword) only appears inside one. A blank line always closes whatever
    /// TRACK block is open.
    /// </summary>
    public class TextFileEmbyLibraryReader : IEmbyLibraryReader
    {
        private readonly string _path;

        public TextFileEmbyLibraryReader(string path) => _path = path;

        public IReadOnlyList<EmbyArtist> ReadAllArtists()
        {
            var lines = File.ReadAllLines(_path);
            return Parse(lines);
        }

        // Exposed for direct testing without touching disk.
        internal static IReadOnlyList<EmbyArtist> Parse(IEnumerable<string> lines)
        {
            var artists = new List<EmbyArtist>();
            EmbyArtist? currentArtist = null;
            EmbyTrackCredit? currentTrack = null;

            void FlushTrack()
            {
                if (currentArtist == null || currentTrack == null) { currentTrack = null; return; }

                foreach (var (role, list) in new[] { ("AlbumArtist", currentTrack.AlbumArtists), ("Artist", currentTrack.Artists), ("Composer", currentTrack.Composers) })
                {
                    if (!list.Any(c => string.Equals(c.Name, currentArtist.DisplayName, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    currentArtist.Tracks.Add(new EmbyTrackCredit
                    {
                        TrackId = currentTrack.TrackId,
                        TrackName = currentTrack.TrackName,
                        AlbumName = currentTrack.AlbumName,
                        AlbumId = currentTrack.AlbumId,
                        Role = role,
                        ProviderIds = currentTrack.ProviderIds,
                        AlbumArtists = currentTrack.AlbumArtists,
                        Artists = currentTrack.Artists,
                        Composers = currentTrack.Composers,
                        Duration = currentTrack.Duration,
                    });
                }
                currentTrack = null;
            }

            void FlushArtist()
            {
                FlushTrack();
                if (currentArtist != null) artists.Add(currentArtist);
                currentArtist = null;
            }

            foreach (var raw in lines)
            {
                var line = raw.Trim();

                if (line.Length == 0)
                {
                    FlushTrack();
                    continue;
                }
                if (line.StartsWith("#")) continue;

                if (currentTrack == null && line.StartsWith("ARTIST ", StringComparison.OrdinalIgnoreCase))
                {
                    FlushArtist();
                    var (id, name, _) = ParseIdAndQuoted(line, "ARTIST");
                    currentArtist = new EmbyArtist { SourceId = id, DisplayName = name };
                    continue;
                }

                if (line.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentArtist == null)
                        throw new FormatException($"TRACK line found before any ARTIST block: \"{line}\"");
                    FlushTrack();
                    var (id, name, _) = ParseIdAndQuoted(line, "TRACK");
                    currentTrack = new EmbyTrackCredit { TrackId = id, TrackName = name };
                    continue;
                }

                if (currentTrack == null)
                    throw new FormatException($"Line found outside any TRACK block: \"{line}\"");

                if (line.StartsWith("ALBUM ", StringComparison.OrdinalIgnoreCase))
                {
                    var (id, name, _) = ParseIdAndQuoted(line, "ALBUM");
                    currentTrack.AlbumId = id;
                    currentTrack.AlbumName = name;
                    continue;
                }

                if (line.StartsWith("DURATION_MS ", StringComparison.OrdinalIgnoreCase))
                {
                    var ms = int.Parse(line.Substring("DURATION_MS ".Length).Trim());
                    currentTrack.Duration = TimeSpan.FromMilliseconds(ms);
                    continue;
                }

                if (line.StartsWith("PROVIDERIDS ", StringComparison.OrdinalIgnoreCase))
                {
                    var body = line.Substring("PROVIDERIDS ".Length).Trim();
                    foreach (var pair in body.Split(','))
                    {
                        var kv = pair.Split(new[] { '=' }, 2);
                        if (kv.Length == 2) currentTrack.ProviderIds[kv[0].Trim()] = kv[1].Trim();
                    }
                    continue;
                }

                if (line.StartsWith("ALBUMARTIST ", StringComparison.OrdinalIgnoreCase))
                {
                    currentTrack.AlbumArtists.Add(ParseCreditedName(line, "ALBUMARTIST"));
                    continue;
                }
                if (line.StartsWith("ARTIST ", StringComparison.OrdinalIgnoreCase))
                {
                    currentTrack.Artists.Add(ParseCreditedName(line, "ARTIST"));
                    continue;
                }
                if (line.StartsWith("COMPOSER ", StringComparison.OrdinalIgnoreCase))
                {
                    currentTrack.Composers.Add(ParseCreditedName(line, "COMPOSER"));
                    continue;
                }

                throw new FormatException($"Unrecognized line: \"{line}\"");
            }

            FlushArtist();
            return artists;
        }

        // "KEYWORD <id> "<quoted name>"" -- id is one whitespace-delimited token.
        private static (string id, string name, string rest) ParseIdAndQuoted(string line, string keyword)
        {
            var afterKeyword = line.Substring(keyword.Length).Trim();
            var firstQuote = afterKeyword.IndexOf('"');
            if (firstQuote < 0) throw new FormatException($"Expected a quoted name in: \"{line}\"");
            var id = afterKeyword.Substring(0, firstQuote).Trim();
            var closingQuote = afterKeyword.IndexOf('"', firstQuote + 1);
            if (closingQuote < 0) throw new FormatException($"Unterminated quoted string in: \"{line}\"");
            var name = afterKeyword.Substring(firstQuote + 1, closingQuote - firstQuote - 1);
            var rest = afterKeyword.Substring(closingQuote + 1).Trim();
            return (id, name, rest);
        }

        // "KEYWORD "<quoted name>" [mbid=<mbid>]"
        private static EmbyCreditedName ParseCreditedName(string line, string keyword)
        {
            var afterKeyword = line.Substring(keyword.Length).Trim();
            var firstQuote = afterKeyword.IndexOf('"');
            var closingQuote = afterKeyword.IndexOf('"', firstQuote + 1);
            if (firstQuote < 0 || closingQuote < 0) throw new FormatException($"Expected a quoted name in: \"{line}\"");
            var name = afterKeyword.Substring(firstQuote + 1, closingQuote - firstQuote - 1);
            var rest = afterKeyword.Substring(closingQuote + 1).Trim();

            string? mbid = null;
            if (rest.StartsWith("mbid=", StringComparison.OrdinalIgnoreCase))
                mbid = rest.Substring("mbid=".Length).Trim();

            return new EmbyCreditedName { Name = name, Mbid = mbid };
        }
    }
}
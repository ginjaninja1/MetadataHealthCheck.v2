using MetadataHealthCheck.v2.Resolvers.MusicBrainz.Client;

namespace MetadataHealthCheck.v2.Fixtures
{
    /// <summary>
    /// Real recorded-response stand-in per §17.1 — no live network calls.
    /// Reproduces §18's worked example: MBID X (correct Sarah Vaughan) and
    /// MBID Y (a different, less-famous same-named artist).
    /// </summary>
    public class FixtureMusicBrainzApiClient : IMusicBrainzApiClient
    {
        public const string MbidX = "mbid-sarah-vaughan-correct";
        public const string MbidY = "mbid-sarah-vaughan-other";

        // Real-world stress-test cases (added per user-supplied examples, verified
        // against external sources where this sandbox's tools could reach them --
        // MusicBrainz's own site blocks automated fetching, so MBIDs/relationship-
        // type strings below are best-effort, not independently confirmed against
        // live MusicBrainz. The surrounding facts (track/album/composer/producer
        // credits) are externally corroborated; see conversation history for sources.
        public const string MbidGusBlack = "mbid-gus-black-bestEffort";
        public const string MbidAFineFrenzy = "mbid-a-fine-frenzy-bestEffort"; // the real recording-artist-credit on "Borrowed Time"
        public const string MbidQueen = "mbid-queen-bestEffort";
        public const string MbidFlorence = "mbid-florence-the-machine-bestEffort";
        public const string MbidAdele = "mbid-adele-bestEffort"; // the real recording-artist-credit on the Del Serino cover
        public const string MbidDelSerino = "mbid-del-serino-bestEffort"; // never surfaces as a candidate today -- see below

        // Confirmed real, 2026-07-12 design review, against the actual MusicBrainz
        // entry (mbid 351d8bdf-33a1-45e2-8c04-c85fad20da55) -- spelling variants of
        // the SAME person, not a rival candidate. Extracted to a shared field so
        // SearchArtist and GetArtistAliases can't drift apart from each other.
        private static readonly List<string> SarahVaughanAliases = new()
        {
            "Sarah Vahghan", "Sarah Voughan", "Sara Vaughan", "Vaughan Sarah", "Sarah Vaughn",
        };

        public IReadOnlyList<MbArtistResult> SearchArtist(string name)
        {
            if (name.Equals("Sarah Vaughan", StringComparison.OrdinalIgnoreCase))
            {
                return new List<MbArtistResult>
                {
                    new MbArtistResult { Mbid = MbidX, Name = "Sarah Vaughan", Score = 100, Aliases = new List<string>(SarahVaughanAliases) },
                    new MbArtistResult { Mbid = MbidY, Name = "Sara Vaughn", Disambiguation = "different, lesser-known artist", Score = 90 },
                };
            }

            if (name.Equals("Gus Black", StringComparison.OrdinalIgnoreCase))
                return new List<MbArtistResult> { new MbArtistResult { Mbid = MbidGusBlack, Name = "Gus Black", Score = 100 } };

            if (name.Equals("Queen", StringComparison.OrdinalIgnoreCase))
                return new List<MbArtistResult> { new MbArtistResult { Mbid = MbidQueen, Name = "Queen", Score = 100 } };

            // Models realistic MB full-text search: any reasonable rendering of the
            // name (and/&/+, case variance) still finds the one canonical entry.
            // The point of this test case isn't the search call itself -- it's
            // whether SoftBucketStrategy's Stage 1 admission gate (edit distance over
            // ArtistNameNormalizer's normalized form) correctly recognizes these as
            // close/near-exact once normalized.
            if (name.ToLowerInvariant().Contains("florence") && name.ToLowerInvariant().Contains("machine"))
                return new List<MbArtistResult> { new MbArtistResult { Mbid = MbidFlorence, Name = "Florence + the Machine", Score = 100 } };

            if (name.Equals("Del Serino", StringComparison.OrdinalIgnoreCase))
                return new List<MbArtistResult> { new MbArtistResult { Mbid = MbidDelSerino, Name = "Del Serino", Score = 100 } };

            // SoftBucketStrategy's artist-search-first rewrite calls this directly as
            // its primary candidate source, admitting a result only if (a) Score
            // clears ScoringConfig.CandidateGeneration.ArtistCandidateMinScore and (b)
            // normalized name-or-alias is within ArtistCandidateMaxEditDistance, then
            // confirming via RecordingLookup against this artist's real tracks before
            // yielding a Candidate. This is exactly why Gus Black and Del Serino still
            // can't be resolved today: each DOES get a real SearchArtist(name) hit
            // (their own MBID passes the name check), but RecordingLookup finds no
            // matching recording for that MBID on any of their tracks (the real
            // recording-artist-credit is A Fine Frenzy / Adele respectively) -- so
            // neither candidate clears confirmation and 0 candidates are generated for
            // either artist. See SearchRecording/GetRelationships comments below for
            // the underlying facts.

            return Array.Empty<MbArtistResult>();
        }

        public IReadOnlyList<MbAlbumTitle> GetReleaseGroupTitles(string artistMbid)
        {
            if (artistMbid == MbidX)
                return new List<MbAlbumTitle> { new MbAlbumTitle { Title = "Crazy and Mixed Up", IsDistinctive = true } };

            return new List<MbAlbumTitle>(); // Y has no overlapping albums
        }

        public IReadOnlyList<MbRecordingResult> SearchRecording(string trackTitle, string? albumTitle, string? artistName = null)
        {
            // Built specifically to exercise RecordingLookup's three-rung fallback ladder
            // end to end: rungs 1 (track+artist+album) and 2 (track+album) both come up
            // empty here on purpose -- only rung 3 (track title alone) finds anything.
            // ArtistCreditText is deliberately the alias "Sarah Vaughn" rather than the
            // primary name "Sarah Vaughan", so this case also exercises MatchedViaAlias.
            if (trackTitle.Equals("Ladder Fallback Case", StringComparison.OrdinalIgnoreCase))
            {
                if (artistName == null && albumTitle == null)
                {
                    return new List<MbRecordingResult>
                    {
                        new MbRecordingResult
                        {
                            RecordingId = "rec-ladder-fallback",
                            ArtistMbid = MbidX,
                            TrackTitle = "Ladder Fallback Case",
                            ReleaseTitle = "An Unlisted Album",
                            TrackTitleMatches = true,
                            ReleaseTitleMatches = false,
                            ArtistCreditText = "Sarah Vaughn",
                        },
                    };
                }
                return Array.Empty<MbRecordingResult>();
            }

            if (trackTitle.Equals("Autumn Leaves", StringComparison.OrdinalIgnoreCase))
            {
                return new List<MbRecordingResult>
                {
                    new MbRecordingResult
                    {
                        RecordingId = "rec-autumn-leaves-X",
                        ArtistMbid = MbidX,
                        TrackTitle = "Autumn Leaves",
                        ReleaseTitle = "Crazy and Mixed Up",
                        TrackTitleMatches = true,
                        ReleaseTitleMatches = albumTitle != null && albumTitle.Equals("Crazy and Mixed Up", StringComparison.OrdinalIgnoreCase),
                        ArtistCreditText = "Sarah Vaughan",
                    },
                    new MbRecordingResult
                    {
                        RecordingId = "rec-autumn-leaves-Y",
                        ArtistMbid = MbidY,
                        TrackTitle = "Autumn Leaves (a different recording)",
                        ReleaseTitle = "Some Other Album",
                        TrackTitleMatches = false,
                        ReleaseTitleMatches = false,
                        ArtistCreditText = "Sara Vaughn",
                    },
                };
            }

            // Sequential Sampler test data (Phase 2): a second, distinct recording for
            // X with no work-relationship credit at all, so a multi-track artist can
            // genuinely exercise more than one sampled observation before resolving.
            if (trackTitle.Equals("A Different Song Entirely", StringComparison.OrdinalIgnoreCase))
            {
                return new List<MbRecordingResult>
                {
                    new MbRecordingResult
                    {
                        RecordingId = "rec-different-song-X",
                        ArtistMbid = MbidX,
                        TrackTitle = "A Different Song Entirely",
                        ReleaseTitle = "Some Earlier Album",
                        TrackTitleMatches = true,
                        ReleaseTitleMatches = false,
                        ArtistCreditText = "Sarah Vaughan",
                    },
                };
            }

            // Gus Black / "Borrowed Time" (real-world example): the recording's real
            // performer/recording-artist-credit is A Fine Frenzy (Alison Sudol),
            // not Gus Black -- he's a work-level composer credit only.
            if (trackTitle.Equals("Borrowed Time", StringComparison.OrdinalIgnoreCase))
            {
                return new List<MbRecordingResult>
                {
                    new MbRecordingResult
                    {
                        RecordingId = "rec-borrowed-time",
                        ArtistMbid = MbidAFineFrenzy,
                        TrackTitle = "Borrowed Time",
                        ReleaseTitle = "One Cell in the Sea",
                        TrackTitleMatches = true,
                        ReleaseTitleMatches = true,
                        ArtistCreditText = "A Fine Frenzy",
                    },
                };
            }

            // Del Serino / "That's It, I Quit, I'm Moving On": same shape as Gus
            // Black above. The real performer-credit on Adele's cover is Adele
            // herself; Del Serino (with Roy Alfred) is a work-level composer credit
            // that never becomes a candidate under today's architecture.
            if (trackTitle.Equals("That's It, I Quit, I'm Moving On", StringComparison.OrdinalIgnoreCase))
            {
                return new List<MbRecordingResult>
                {
                    new MbRecordingResult
                    {
                        RecordingId = "rec-thats-it-i-quit",
                        ArtistMbid = MbidAdele,
                        TrackTitle = "That's It, I Quit, I'm Moving On",
                        ReleaseTitle = "19",
                        TrackTitleMatches = true,
                        ReleaseTitleMatches = true,
                        ArtistCreditText = "Adele",
                    },
                };
            }

            // Florence + the Machine's own song, used purely to generate a real
            // candidate for the naming-variant test below.
            if (trackTitle.Equals("Dog Days Are Over", StringComparison.OrdinalIgnoreCase))
            {
                return new List<MbRecordingResult>
                {
                    new MbRecordingResult
                    {
                        RecordingId = "rec-dog-days-are-over",
                        ArtistMbid = MbidFlorence,
                        TrackTitle = "Dog Days Are Over",
                        ReleaseTitle = "Lungs",
                        TrackTitleMatches = true,
                        ReleaseTitleMatches = true,
                        ArtistCreditText = "Florence + the Machine",
                    },
                };
            }

            // Queen / "Bohemian Rhapsody" on a non-canonical radio compilation:
            // a recording is found (the performer credit is real and unambiguous).
            // See GetRelationships below for the real producer credit on this one.
            if (trackTitle.Equals("Bohemian Rhapsody", StringComparison.OrdinalIgnoreCase))
            {
                return new List<MbRecordingResult>
                {
                    new MbRecordingResult
                    {
                        RecordingId = "rec-bohemian-rhapsody-queen",
                        ArtistMbid = MbidQueen,
                        TrackTitle = "Bohemian Rhapsody",
                        ReleaseTitle = "Top 2000 (Radio 2)",
                        TrackTitleMatches = true,
                        ReleaseTitleMatches = false, // not a canonical release -- no real release-group match
                        ArtistCreditText = "Queen",
                    },
                };
            }

            return Array.Empty<MbRecordingResult>();
        }

        public IReadOnlyList<MbRelationship> GetRelationships(string recordingId)
        {
            if (recordingId == "rec-autumn-leaves-X")
                return new List<MbRelationship> { new MbRelationship { RelationshipType = "writer", ArtistMbid = MbidX, Level = RelationshipLevel.Work } };

            // Gus Black's real, externally-corroborated composer credit on "Borrowed
            // Time" -- present in the data, exactly as MusicBrainz would show it, but
            // structurally unreachable: WorkRelationshipEvidenceCollector only checks
            // relationships whose ArtistMbid matches the CANDIDATE it's scoring, and
            // the only candidate this recording can ever produce is A Fine Frenzy
            // (see SearchRecording above). Phase 3's Strategy C ("borrowed anchor") is
            // the planned fix, not a bug in this fixture.
            if (recordingId == "rec-borrowed-time")
                return new List<MbRelationship> { new MbRelationship { RelationshipType = "composer", ArtistMbid = MbidGusBlack, Level = RelationshipLevel.Work } };

            // Del Serino (with Roy Alfred) similarly: real credit, same structural
            // unreachability as Gus Black above.
            if (recordingId == "rec-thats-it-i-quit")
                return new List<MbRelationship>
                {
                    new MbRelationship { RelationshipType = "composer", ArtistMbid = MbidDelSerino, Level = RelationshipLevel.Work },
                    new MbRelationship { RelationshipType = "writer", ArtistMbid = "mbid-roy-alfred-bestEffort", Level = RelationshipLevel.Work },
                };

            // Queen co-produced "Bohemian Rhapsody" with Roy Thomas Baker -- a real,
            // externally-corroborated recording-level producer credit attributed to
            // the band, added 2026-07-13 alongside RecordingRelationshipEvidenceCollector.
            // This is exactly the case that collector was motivated by: a performer-only
            // (Artist-tier) band credit has no writer/composer relation to find, but a
            // real recording-level relation does exist.
            if (recordingId == "rec-bohemian-rhapsody-queen")
                return new List<MbRelationship>
                {
                    new MbRelationship { RelationshipType = "producer", ArtistMbid = MbidQueen, Level = RelationshipLevel.Recording },
                    new MbRelationship { RelationshipType = "producer", ArtistMbid = "mbid-roy-thomas-baker-bestEffort", Level = RelationshipLevel.Recording },
                };

            return Array.Empty<MbRelationship>();
        }

        public string GetArtistDisplayName(string artistMbid)
        {
            if (artistMbid == MbidX) return "Sarah Vaughan";
            if (artistMbid == MbidY) return "Sara Vaughn";
            if (artistMbid == MbidGusBlack) return "Gus Black"; // never actually surfaces as a candidate today -- see SearchRecording
            if (artistMbid == MbidAFineFrenzy) return "A Fine Frenzy";
            if (artistMbid == MbidQueen) return "Queen";
            if (artistMbid == MbidFlorence) return "Florence + the Machine"; // MB's own display form; sort name is "Florence and the Machine"
            if (artistMbid == MbidAdele) return "Adele";
            if (artistMbid == MbidDelSerino) return "Del Serino"; // never actually surfaces as a candidate today -- see SearchRecording
            return "Unknown Artist";
        }

        public IReadOnlyList<string> GetArtistAliases(string artistMbid)
        {
            if (artistMbid == MbidX) return new List<string>(SarahVaughanAliases);
            return Array.Empty<string>(); // everyone else has no registered aliases modeled
        }
    }
}
namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Evidence
{
    /// <summary>
    /// Which round-type(s) a rung's confirmation walk is allowed to run,
    /// decided by the caller (RecordingCorroborationEvidenceCollector) from the
    /// observation's bucket -- every rung in the ladder for a given observation
    /// runs under the same mode.
    ///
    /// PerformerOnly: run the cheap performer-credit round only; never call
    /// GetRelationships for this observation. Used for AlbumArtist/Artist
    /// buckets, where a composer-only artist structurally can never confirm as
    /// a performer, so a relationship-scan round would only ever be wasted work.
    ///
    /// RelationshipOnly: skip the cheap round; go straight to the
    /// relationship-scan round for every rung. Used for the Composer bucket,
    /// where a composer is never the recording's credited performer.
    /// </summary>
    public enum ConfirmationMode
    {
        PerformerOnly,
        RelationshipOnly,
    }
}

namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Config
{
    /// <summary>
    /// One replacement/allowance rule in the admission-gate name normalization
    /// table. Pattern is a case-insensitive regex; Replacement is applied via
    /// Regex.Replace. Kept as data, not hardcoded logic, so the table is
    /// genuinely editable. Diacritics folding is not one of these rules -- it's
    /// a fixed Unicode-normalization step applied before the rule list runs
    /// (see ArtistNameNormalizer), since it isn't meaningfully expressible as a
    /// small edited table the way "strip leading The" is.
    /// </summary>
    public class NameNormalizationRule
    {
        public string Pattern { get; set; } = "";
        public string Replacement { get; set; } = "";
    }
}

namespace MetadataHealthCheck.v2.Core.Interfaces
{
    /// <summary>
    /// The minimal shape Core's engine and sampler need from a resolver's tuning
    /// config, so Core never depends on any resolver's concrete config class.
    /// A resolver's own config type implements this and adds whatever further
    /// tunables it needs; those extra members are visible only to that resolver's
    /// own scorer/decision gate/strategies, never to Core.
    /// </summary>
    public interface IScoringConfig
    {
        IReadOnlyDictionary<string, double> EvidenceWeights { get; }
        IReadOnlyDictionary<string, int> BucketCeiling { get; }
    }
}

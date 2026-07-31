using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Core.Interfaces
{
    // Optional, pathway-local candidate narrowing for one specific bucket.
    // Deliberately opaque to the sequential sampler/Core: it knows nothing about
    // what a bucketKey or a candidate's Type means, it just relays the bucket's
    // own key string and the currently-live candidate list to whatever the
    // plugin registered, and uses whatever comes back for that bucket's
    // collection loop only. Does not touch evidence/scoring for any candidate --
    // a filtered-out candidate simply collects no new evidence in that bucket;
    // its running LLR from other buckets is untouched. Null on the plugin means
    // no filtering anywhere, for any bucket.
    public interface IBucketCandidateFilter
    {
        IReadOnlyList<Candidate> Filter(string bucketKey, IReadOnlyList<Candidate> liveCandidates, ResolutionContext context);
    }
}

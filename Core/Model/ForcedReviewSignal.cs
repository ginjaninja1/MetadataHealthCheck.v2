namespace MetadataHealthCheck.v2.Core.Model
{
    // Lets a resolver override the decision gate's normal accept/reject outcome
    // and force needs_review instead, with a human-readable reason -- for cases
    // where the resolver knows something the LLR/margin math alone can't
    // capture (e.g. two independently-scored candidates turning out to
    // represent the same real-world identity). Core defines this mechanism but
    // has no concept of what triggers it -- that's entirely up to each
    // resolver's own candidate-generation/evidence-collection code.
    //
    // Set via ResolutionContext.SetExtension(new ForcedReviewSignal(...)) at
    // any point during one resolution; ResolutionEngine checks for it via
    // GetExtension<ForcedReviewSignal> after the decision gate runs, before
    // any auto-accept identity-cache write, so a forced review is never cached
    // as confirmed.
    public class ForcedReviewSignal
    {
        public string Reason { get; }
        public IReadOnlyList<string> Notes { get; }

        public ForcedReviewSignal(string reason, IReadOnlyList<string> notes)
        {
            Reason = reason;
            Notes = notes;
        }
    }
}
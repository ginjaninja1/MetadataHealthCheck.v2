using MetadataHealthCheck.v2.Core.Interfaces;

namespace MetadataHealthCheck.v2.Core.Model
{
    // Added 2026-07-19: mechanical cross-check between what evidence collectors
    // declare they can emit (IEvidenceCollector<T>/IObservationEvidenceCollector<T>'s
    // PossibleWeightedEvidenceTypes) and what ScoringConfig.EvidenceWeights actually has
    // entries for. Exists specifically because this exact drift -- a collector stops
    // emitting an evidence type, or never emitted the one a weight entry implies, with
    // nothing catching it -- has already happened three separate times undetected
    // (NameSimilarity.*, WorkRelationship.*, ProviderIds.Confirmed all went dead
    // without anything flagging it; each was only found by manual grepping). This is a
    // safety net (Option A of three considered), not a redesign of how weights are
    // authored or tuned -- ScoringConfig.EvidenceWeights stays a plain dictionary,
    // still eyeballed/tuned in one file. See Option B (compile-time-safe keys) and
    // Option C (decentralized weight ownership) if this alone proves insufficient.
    public static class EvidenceConfigValidator
    {
        public class Finding
        {
            public string Severity { get; set; } = ""; // "OrphanedWeight" or "UndeclaredWeight"
            public string EvidenceType { get; set; } = "";
            public string Detail { get; set; } = "";
        }

        // OrphanedWeight: a ScoringConfig.EvidenceWeights key that no registered
        // collector declares it can ever emit -- almost certainly dead config, the
        // exact bug found three times so far.
        //
        // UndeclaredWeight: a collector declares (in PossibleWeightedEvidenceTypes) an
        // evidence type with NO matching ScoringConfig.EvidenceWeights entry -- the
        // inverse failure mode, not yet observed in this codebase but just as real a
        // risk: a collector could start emitting a new or renamed type before a weight
        // exists for it, or after a typo, and score as whatever an unrecognized
        // dictionary key resolves to (worth confirming what SequentialSampler actually
        // does on a missing key -- 0? throw? -- separately from this check).
        public static IReadOnlyList<Finding> Validate<TSourceEntity>(
            IEnumerable<IEvidenceCollector<TSourceEntity>> evidenceCollectors,
            IEnumerable<IObservationEvidenceCollector<TSourceEntity>> observationEvidenceCollectors,
            IReadOnlyDictionary<string, double> evidenceWeights)
            where TSourceEntity : ISourceEntity
        {
            var declared = new HashSet<string>();
            foreach (var c in evidenceCollectors)
                foreach (var t in c.PossibleWeightedEvidenceTypes)
                    declared.Add(t);
            foreach (var c in observationEvidenceCollectors)
                foreach (var t in c.PossibleWeightedEvidenceTypes)
                    declared.Add(t);

            var findings = new List<Finding>();

            foreach (var key in evidenceWeights.Keys)
            {
                if (!declared.Contains(key))
                {
                    findings.Add(new Finding
                    {
                        Severity = "OrphanedWeight",
                        EvidenceType = key,
                        Detail = $"ScoringConfig.EvidenceWeights[\"{key}\"] = {evidenceWeights[key]}, but no registered collector's PossibleWeightedEvidenceTypes declares it -- likely dead config.",
                    });
                }
            }

            foreach (var type in declared)
            {
                if (!evidenceWeights.ContainsKey(type))
                {
                    findings.Add(new Finding
                    {
                        Severity = "UndeclaredWeight",
                        EvidenceType = type,
                        Detail = $"A registered collector declares \"{type}\" in PossibleWeightedEvidenceTypes, but ScoringConfig.EvidenceWeights has no matching entry -- if this is ever emitted with Contributing=true, confirm what the scorer does with an unrecognized key.",
                    });
                }
            }

            return findings;
        }
    }
}
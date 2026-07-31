using MetadataHealthCheck.v2.Core.Interfaces;

namespace MetadataHealthCheck.v2.Core.Validation
{
    // Mechanical cross-check between what evidence collectors declare they can
    // emit (PossibleWeightedEvidenceTypes) and what a resolver's evidence-weight
    // configuration actually has entries for. This drift -- a collector stops
    // emitting an evidence type, or a weight entry implies one no collector ever
    // emits -- is otherwise silent and only found by manual grepping.
    public static class EvidenceConfigValidator
    {
        public class Finding
        {
            public string Severity { get; set; } = ""; // "OrphanedWeight" or "UndeclaredWeight"
            public string EvidenceType { get; set; } = "";
            public string Detail { get; set; } = "";
        }

        // OrphanedWeight: a configured evidence-weight key that no registered
        // collector declares it can ever emit -- almost certainly dead config.
        //
        // UndeclaredWeight: a collector declares an evidence type with no
        // matching weight entry -- the inverse failure mode: a collector could
        // start emitting a new or renamed type before a weight exists for it.
        public static IReadOnlyList<Finding> Validate<TSourceEntity>(
            IEnumerable<IEvidenceCollector<TSourceEntity>> evidenceCollectors,
            IEnumerable<IObservationEvidenceCollector<TSourceEntity>> observationEvidenceCollectors,
            IEnumerable<IRoundBasedObservationEvidenceCollector<TSourceEntity>> roundBasedObservationEvidenceCollectors,
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
            foreach (var c in roundBasedObservationEvidenceCollectors)
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
                        Detail = $"EvidenceWeights[\"{key}\"] = {evidenceWeights[key]}, but no registered collector's PossibleWeightedEvidenceTypes declares it -- likely dead config.",
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
                        Detail = $"A registered collector declares \"{type}\" in PossibleWeightedEvidenceTypes, but EvidenceWeights has no matching entry.",
                    });
                }
            }

            return findings;
        }
    }
}

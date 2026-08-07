using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MetadataHealthCheck.v2.BatchHarness
{
    /// <summary>
    /// One artist's result from a batch run, flattened into whatever a CSV row
    /// or console summary needs. Deliberately a plain data holder -- no logic
    /// beyond what's needed to construct it lives here.
    /// </summary>
    public class BatchResultRow
    {
        public string ArtistName { get; set; } = "";
        public string SourceId { get; set; } = "";
        public string ExpectedMbid { get; set; } = "";
        public string ChosenMbid { get; set; } = "";
        public string ExpectedUrl => HasExpectedMbid ? $"https://musicbrainz.org/artist/{ExpectedMbid}" : "";
        public string ChosenUrl => !string.IsNullOrWhiteSpace(ChosenMbid) ? $"https://musicbrainz.org/artist/{ChosenMbid}" : "";
        public string Decision { get; set; } = "";           // MatchResult.Status: auto_accept | auto_reject | needs_review
        // Added 2026-07-28: MatchResult.DecisionReason, e.g.
        // "forced_needs_review_candidate_fold" when ResolutionEngine's fold-override
        // (see ArtistCandidateStrategy's fold-pass doc comment) downgraded an otherwise
        // auto_accept/auto_reject decision. Null/empty for a decision that was never
        // overridden -- i.e. ThresholdDecisionGate's own status stands unchanged.
        public string? DecisionReason { get; set; }
        public double Confidence { get; set; }
        public double Llr { get; set; }
        public double Margin { get; set; }
        public int ApiCalls { get; set; }
        public int CacheHits { get; set; }
        public long ElapsedMs { get; set; }
        public string? Error { get; set; }                   // set if this artist's resolution threw

        // Added 2026-07-27 (Composer-relationship-pathway split): how many
        // CONTRIBUTING evidence records this artist produced, broken down by
        // "{Role}.{EvidenceType}.{ViaRelationship|ViaPerformer}" -- e.g.
        // "Composer.CorroborationTier.Tier2.ViaRelationship" or
        // "Artist.CorroborationTier.Tier1.ViaPerformer". Populated by BatchHarness/
        // Program.cs from InMemoryMatchRepository.Evidence right after ResolveOne
        // returns (the repo is fresh per artist, so everything in it belongs to this
        // one resolution). Deliberately opaque/generic here -- AccuracyReport has no
        // idea what a "Composer" or "ViaRelationship" means, it just reports whatever
        // keys show up, the same way EvidenceRecord.EvidenceType itself is an opaque
        // string to Core. Empty for any artist that produced no observation-level
        // evidence at all (static-only resolution, or zero candidates).
        public IReadOnlyDictionary<string, int> EvidenceCountsByKey { get; set; } = new Dictionary<string, int>();

        // Correctness is judged purely on "did the chosen/top candidate match the
        // known-correct MBID", regardless of decision status. This deliberately
        // does NOT attempt to judge whether an auto_reject was "correct" in the
        // sense of "no right answer existed to find" -- MatchResult doesn't expose
        // enough about what was rejected and why to make that call safely. Treat
        // auto_reject rows' Correct value as informational only; the important
        // signal for auto_reject is the raw Decision distribution, not this flag.
        public bool HasExpectedMbid => !string.IsNullOrWhiteSpace(ExpectedMbid);
        public bool Correct => HasExpectedMbid
            && string.Equals(ExpectedMbid, ChosenMbid, StringComparison.OrdinalIgnoreCase);

        // The one figure that matters most (§ discussion with Nick): an auto_accept
        // that is WRONG is a confidently-wrong answer written back to the library
        // with nothing flagging it for review. Every other wrong answer at least
        // leaves a trail (needs_review) or produces no answer at all (auto_reject).
        public bool IsFalseAutoAccept => HasExpectedMbid
            && string.Equals(Decision, "auto_accept", StringComparison.OrdinalIgnoreCase)
            && !Correct;
    }

    public static class AccuracyReport
    {
        public static void WriteCsv(string path, IReadOnlyList<BatchResultRow> rows)
        {
            // Added 2026-07-27: sparse evidence-count columns, one per distinct
            // "{Role}.{EvidenceType}.{ViaX}" key seen ANYWHERE across this run's rows
            // -- not a fixed pre-declared column list, since the whole point is that
            // new evidence types/buckets shouldn't require a CSV-writer change to show
            // up. Sorted for a stable column order run-to-run. An artist that never
            // produced a given key gets "0" in that column, not a blank -- this is a
            // count, and 0 is a real, meaningful answer ("this pathway never fired for
            // this artist"), unlike the free-text fields above where blank means N/A.
            var evidenceKeys = rows
                .SelectMany(r => r.EvidenceCountsByKey.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var header = "ArtistName,SourceId,ExpectedMbid,ExpectedUrl,ChosenMbid,ChosenUrl,Decision,DecisionReason,Correct,Confidence,Llr,Margin,ApiCalls,CacheHits,ElapsedMs,Error"
                + (evidenceKeys.Count > 0 ? "," + string.Join(",", evidenceKeys.Select(CsvEscape)) : "");
            var lines = new List<string> { header };

            foreach (var r in rows)
            {
                var fixedFields = new[]
                {
                    CsvEscape(r.ArtistName),
                    CsvEscape(r.SourceId),
                    CsvEscape(r.ExpectedMbid),
                    CsvEscape(r.ExpectedUrl),
                    CsvEscape(r.ChosenMbid),
                    CsvEscape(r.ChosenUrl),
                    CsvEscape(r.Decision),
                    CsvEscape(r.DecisionReason ?? ""),
                    r.HasExpectedMbid ? r.Correct.ToString() : "",
                    r.Confidence.ToString("F4", CultureInfo.InvariantCulture),
                    r.Llr.ToString("F4", CultureInfo.InvariantCulture),
                    r.Margin.ToString("F4", CultureInfo.InvariantCulture),
                    r.ApiCalls.ToString(CultureInfo.InvariantCulture),
                    r.CacheHits.ToString(CultureInfo.InvariantCulture),
                    r.ElapsedMs.ToString(CultureInfo.InvariantCulture),
                    CsvEscape(r.Error ?? ""),
                };
                var evidenceFields = evidenceKeys.Select(k => r.EvidenceCountsByKey.TryGetValue(k, out var c) ? c.ToString(CultureInfo.InvariantCulture) : "0");
                lines.Add(string.Join(",", fixedFields.Concat(evidenceFields)));
            }
            File.WriteAllLines(path, lines);
        }

        public static void PrintConsoleSummary(IReadOnlyList<BatchResultRow> rows)
        {
            var errored = rows.Where(r => r.Error != null).ToList();
            var scored = rows.Where(r => r.Error == null).ToList();
            var withGroundTruth = scored.Where(r => r.HasExpectedMbid).ToList();
            var withoutGroundTruth = scored.Where(r => !r.HasExpectedMbid).ToList();

            Console.WriteLine();
            Console.WriteLine(new string('=', 78));
            Console.WriteLine("BATCH ACCURACY REPORT");
            Console.WriteLine(new string('=', 78));
            Console.WriteLine($"Total artists processed : {rows.Count}");
            Console.WriteLine($"  Errored (excluded below): {errored.Count}");
            Console.WriteLine($"  No ground-truth MBID (excluded from accuracy, included in efficiency): {withoutGroundTruth.Count}");
            Console.WriteLine($"  Scored against ground truth: {withGroundTruth.Count}");

            if (errored.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Errored artists:");
                foreach (var r in errored.Take(20))
                    Console.WriteLine($"  {r.ArtistName} ({r.SourceId}): {r.Error}");
                if (errored.Count > 20)
                    Console.WriteLine($"  ... and {errored.Count - 20} more");
            }

            if (withGroundTruth.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("--- Confusion: Decision x Correct ---");
                var byDecision = withGroundTruth.GroupBy(r => r.Decision, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count());
                foreach (var g in byDecision)
                {
                    var correct = g.Count(r => r.Correct);
                    var wrong = g.Count() - correct;
                    Console.WriteLine($"  {g.Key,-14} total={g.Count(),-5} correct={correct,-5} wrong={wrong}");
                }

                var falseAutoAccepts = withGroundTruth.Count(r => r.IsFalseAutoAccept);
                var totalAutoAccepts = withGroundTruth.Count(r => string.Equals(r.Decision, "auto_accept", StringComparison.OrdinalIgnoreCase));
                Console.WriteLine();
                Console.WriteLine($"  *** False auto-accepts (confidently WRONG, no review flag): {falseAutoAccepts} / {totalAutoAccepts} auto-accepts ***");

                var overallAccuracy = (double)withGroundTruth.Count(r => r.Correct) / withGroundTruth.Count;
                Console.WriteLine($"  Overall accuracy (top candidate == expected, any decision): {overallAccuracy:P1}");

                // Added 2026-07-28: how many rows carry a non-empty DecisionReason
                // (currently only "forced_needs_review_candidate_fold", set by
                // ResolutionEngine's fold-override -- see ArtistCandidateStrategy's fold-pass
                // doc comment). Answers "how much of the needs_review total is an
                // override, versus a genuine LLR/margin shortfall" without needing to
                // open the CSV.
                var withReason = scored.Where(r => !string.IsNullOrEmpty(r.DecisionReason)).ToList();
                if (withReason.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("--- Decision overrides (DecisionReason) ---");
                    foreach (var g in withReason.GroupBy(r => r.DecisionReason, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()))
                    {
                        Console.WriteLine($"  {g.Key,-40} total={g.Count()}");
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine("--- Efficiency ---");
            if (scored.Count > 0)
            {
                Console.WriteLine($"  Avg API calls/artist : {scored.Average(r => r.ApiCalls):F2}");
                Console.WriteLine($"  Avg cache hits/artist: {scored.Average(r => r.CacheHits):F2}");
                Console.WriteLine($"  Avg elapsed ms/artist: {scored.Average(r => r.ElapsedMs):F0}");
                foreach (var g in scored.GroupBy(r => r.Decision, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()))
                {
                    Console.WriteLine($"    {g.Key,-14} avg API calls={g.Average(r => r.ApiCalls):F2}  avg ms={g.Average(r => r.ElapsedMs):F0}  n={g.Count()}");
                }
            }

            // Added 2026-07-27: aggregate, run-wide totals per "{Role}.{EvidenceType}.
            // {ViaX}" key -- the batch-level question this answers is "is each pathway
            // (e.g. Composer/RelationshipOnly) actually firing at all, and how often
            // does it end up being what confirms the candidate", not any one artist's
            // detail (that's what the CSV's sparse columns are for). Summed across ALL
            // scored rows (errored rows excluded, same as the rest of this report).
            var evidenceTotals = scored
                .SelectMany(r => r.EvidenceCountsByKey)
                .GroupBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Key: g.Key, Total: g.Sum(kvp => kvp.Value), ArtistsWithAny: g.Count(kvp => kvp.Value > 0)))
                .OrderByDescending(x => x.Total)
                .ToList();

            if (evidenceTotals.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("--- Evidence pathway usage (contributing evidence only) ---");
                foreach (var (key, total, artistsWithAny) in evidenceTotals)
                {
                    Console.WriteLine($"  {key,-68} total={total,-6} artists-with-any={artistsWithAny}");
                }
            }

            Console.WriteLine(new string('=', 78));
        }

        private static string CsvEscape(string field)
        {
            if (field.Contains(",") || field.Contains("\"") || field.Contains("\n"))
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            return field;
        }
    }
}
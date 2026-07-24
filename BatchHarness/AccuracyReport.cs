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
        public string Decision { get; set; } = "";           // MatchResult.Status: auto_accept | auto_reject | needs_review
        public double Confidence { get; set; }
        public double Margin { get; set; }
        public int ApiCalls { get; set; }
        public long ElapsedMs { get; set; }
        public string? Error { get; set; }                   // set if this artist's resolution threw

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
            var lines = new List<string>
            {
                "ArtistName,SourceId,ExpectedMbid,ChosenMbid,Decision,Correct,Confidence,Margin,ApiCalls,ElapsedMs,Error"
            };
            foreach (var r in rows)
            {
                lines.Add(string.Join(",", new[]
                {
                    CsvEscape(r.ArtistName),
                    CsvEscape(r.SourceId),
                    CsvEscape(r.ExpectedMbid),
                    CsvEscape(r.ChosenMbid),
                    CsvEscape(r.Decision),
                    r.HasExpectedMbid ? r.Correct.ToString() : "",
                    r.Confidence.ToString("F4", CultureInfo.InvariantCulture),
                    r.Margin.ToString("F4", CultureInfo.InvariantCulture),
                    r.ApiCalls.ToString(CultureInfo.InvariantCulture),
                    r.ElapsedMs.ToString(CultureInfo.InvariantCulture),
                    CsvEscape(r.Error ?? ""),
                }));
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
            }

            Console.WriteLine();
            Console.WriteLine("--- Efficiency ---");
            if (scored.Count > 0)
            {
                Console.WriteLine($"  Avg API calls/artist : {scored.Average(r => r.ApiCalls):F2}");
                Console.WriteLine($"  Avg elapsed ms/artist: {scored.Average(r => r.ElapsedMs):F0}");
                foreach (var g in scored.GroupBy(r => r.Decision, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()))
                {
                    Console.WriteLine($"    {g.Key,-14} avg API calls={g.Average(r => r.ApiCalls):F2}  avg ms={g.Average(r => r.ElapsedMs):F0}  n={g.Count()}");
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
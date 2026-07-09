namespace MetadataHealthCheck.v2.Diagnostics
{
    /// <summary>
    /// §15.5: single call site, two outputs. Phase 1 sink is Console (plus an
    /// in-memory buffer for test assertions) — the real Emby ILogger-scoped
    /// sink is wired once the exact "get a logger scoped by name" call is
    /// confirmed against a real Emby host (§15.1's listed unverified item,
    /// §20.4).
    /// </summary>
    public class StructuredLogger
    {
        public List<string> Lines { get; } = new();

        public void Log(string level, string component, string message, params object[] args)
        {
            var formatted = args.Length > 0 ? string.Format(message, args) : message;
            var line = $"[{level}] [{component}] {formatted}";
            Lines.Add(line);
            Console.WriteLine(line);
        }

        public void Info(string component, string message, params object[] args) => Log("Info", component, message, args);
        public void Warn(string component, string message, params object[] args) => Log("Warn", component, message, args);
        public void Debug(string component, string message, params object[] args) => Log("Debug", component, message, args);
        public void ErrorException(string component, string message, Exception ex, params object[] args)
            => Log("ErrorException", component, message + " | " + ex, args);
    }
}

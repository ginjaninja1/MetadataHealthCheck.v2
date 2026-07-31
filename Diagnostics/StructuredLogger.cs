namespace MetadataHealthCheck.v2.Diagnostics
{
    /// <summary>
    /// Single logging call site with two outputs: an in-memory line buffer
    /// (for test assertions and dumping a failing entity's trace on error) and,
    /// optionally, Console. Console output can be suppressed per instance so
    /// callers running many loggers concurrently against one console can keep
    /// only the in-memory buffer.
    /// </summary>
    public class StructuredLogger
    {
        private readonly bool _writeToConsole;

        public StructuredLogger(bool writeToConsole = true)
        {
            _writeToConsole = writeToConsole;
        }

        public List<string> Lines { get; } = new();

        public void Log(string level, string component, string message, params object[] args)
        {
            var formatted = args.Length > 0 ? string.Format(message, args) : message;
            var line = $"[{level}] [{component}] {formatted}";
            Lines.Add(line);
            if (_writeToConsole) Console.WriteLine(line);
        }

        public void Info(string component, string message, params object[] args) => Log("Info", component, message, args);
        public void Warn(string component, string message, params object[] args) => Log("Warn", component, message, args);
        public void Debug(string component, string message, params object[] args) => Log("Debug", component, message, args);
        public void ErrorException(string component, string message, Exception ex, params object[] args)
            => Log("ErrorException", component, message + " | " + ex, args);
    }
}
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace PrepForge.DeployAgent;

public sealed class SimpleLogFormatter : ConsoleFormatter
{
    public const string FormatterName = "simple-clean";

    public SimpleLogFormatter(IOptions<SimpleConsoleFormatterOptions> options)
        : base(FormatterName) { }

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var level = logEntry.LogLevel switch
        {
            LogLevel.Trace       => "trce",
            LogLevel.Debug       => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning     => "warn",
            LogLevel.Error       => "fail",
            LogLevel.Critical    => "crit",
            _                    => "none"
        };

        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        textWriter.WriteLine($"{timestamp} {level}: {message}");

        if (logEntry.Exception is not null)
            textWriter.WriteLine(logEntry.Exception);
    }
}

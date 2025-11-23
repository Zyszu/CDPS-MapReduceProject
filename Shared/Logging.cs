namespace Shared.Logging;

using Serilog;

public static class Logger
{
    private static bool _initialized = false;

    public static void Init(string? nodeName = null)
    {
        if (_initialized) return;

        var name = string.IsNullOrEmpty(nodeName) ? "Node" : nodeName;

        Log.Logger = new LoggerConfiguration()
            .Enrich.WithProperty("Node", name)
            .WriteTo.Console()
            .WriteTo.File("logs/log.txt", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        _initialized = true;
    }

    public static void Info(string message)
    {
        Log.Information(message);
    }

    public static void Warn(string message)
    {
        Log.Warning(message);
    }

    public static void Error(string message)
    {
        Log.Error(message);
    }

    public static void Fatal(string message)
    {
        Log.Fatal(message);
    }

    public static void Debug(string message)
    {
        Log.Debug(message);
    }
}

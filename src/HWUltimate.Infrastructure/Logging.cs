using Serilog;
using Serilog.Events;

namespace HWUltimate.Infrastructure;

public static class Logging
{
    public static ILogger CreateLogger(AppPaths paths, bool verbose = false) =>
        new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("App", "HWUltimate")
            .WriteTo.File(
                Path.Combine(paths.LogDirectory, "hwultimate-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 20 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
}

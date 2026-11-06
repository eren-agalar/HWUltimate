using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NovaHardware.Core.Settings;

namespace NovaHardware.Infrastructure.Settings;

/// <summary>Persists <see cref="AppSettings"/> as JSON with atomic replace so a crash never corrupts the file.</summary>
public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ILogger<JsonSettingsService> _logger;

    public JsonSettingsService(string path, ILogger<JsonSettingsService> logger)
    {
        _path = path;
        _logger = logger;
    }

    public AppSettings Current { get; private set; } = new();

    public event EventHandler<AppSettings>? Changed;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (File.Exists(_path))
            {
                var text = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(text);
                if (SettingsMigration.IsLegacy(doc.RootElement))
                {
                    // v0.1 flat file: keep a copy, convert, and write the v0.2 structure.
                    File.Copy(_path, Path.ChangeExtension(_path, ".v1.json"), overwrite: true);
                    Current = SettingsMigration.FromV1(doc.RootElement);
                    _logger.LogInformation("Migrated v0.1 settings to schema {Schema}", AppSettings.CurrentSchema);
                    await SaveAsync(Current, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    Current = doc.RootElement.Deserialize<AppSettings>(Options) ?? new AppSettings();
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Settings file unreadable; using defaults. A backup is kept at {Path}.bak", _path);
            TryBackup();
            Current = new AppSettings();
        }

        return Current;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var tmp = _path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tmp, _path, overwrite: true);
        Current = settings;
        Changed?.Invoke(this, settings);
    }

    private void TryBackup()
    {
        try
        {
            File.Copy(_path, _path + ".bak", overwrite: true);
        }
        catch (IOException)
        {
        }
    }
}

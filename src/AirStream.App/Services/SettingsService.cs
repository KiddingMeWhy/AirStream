using System.IO;
using System.Text.Json;
using AirStream.Core.Models;

namespace AirStream.App.Services;

public sealed class SettingsService
{
    private readonly string _settingsDirectory;
    private readonly string _settingsFile;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public SettingsService()
    {
        _settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AirStream");

        _settingsFile = Path.Combine(
            _settingsDirectory,
            "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsFile))
                return new AppSettings();

            var json = File.ReadAllText(_settingsFile);

            return JsonSerializer.Deserialize<AppSettings>(
                json,
                _jsonOptions) ?? new AppSettings();
        }
        catch
        {
            // Bei einer fehlerhaften Einstellungsdatei
            // startet AirStream mit Standardeinstellungen.
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(_settingsDirectory);

            var json = JsonSerializer.Serialize(
                settings,
                _jsonOptions);

            var temporaryFile = _settingsFile + ".tmp";

            File.WriteAllText(temporaryFile, json);

            File.Move(
                temporaryFile,
                _settingsFile,
                overwrite: true);
        }
        catch
        {
            // Ein Fehler beim Speichern soll die App
            // nicht zum Absturz bringen.
        }
    }
}
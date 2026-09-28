using System.Text.Json;

namespace TerminalRearranger;

internal sealed class UserSettings
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public string? SelectedDisplay { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerminalRearranger", "settings.json");

    internal static UserSettings Load()
    {
        try { return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, FilePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Keep the widget usable. */ }
    }
}

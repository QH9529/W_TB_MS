using System.Text.Json;

namespace W_TB_jiankong.Models
{
    internal sealed class ArchiveSettings
    {
        public string ArchiveFolder { get; set; } = "";
    }

    internal static class ArchiveSettingsStore
    {
        private static readonly string SettingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "W_TB_MS");

        private static readonly string SettingsPath = Path.Combine(SettingsFolder, "settings.json");
        private static readonly string LegacySettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "W_TB_jiankong",
            "settings.json");

        internal static string LoadArchiveFolder()
        {
            string defaultFolder = GetDefaultArchiveFolder();
            try
            {
                string? sourcePath = File.Exists(SettingsPath)
                    ? SettingsPath
                    : File.Exists(LegacySettingsPath) ? LegacySettingsPath : null;
                if (sourcePath != null)
                {
                    ArchiveSettings? settings = JsonSerializer.Deserialize<ArchiveSettings>(
                        File.ReadAllText(sourcePath));
                    if (!string.IsNullOrWhiteSpace(settings?.ArchiveFolder))
                    {
                        Directory.CreateDirectory(settings.ArchiveFolder);
                        string archiveFolder = Path.GetFullPath(settings.ArchiveFolder);
                        if (sourcePath == LegacySettingsPath)
                            SaveArchiveFolder(archiveFolder);
                        return archiveFolder;
                    }
                }
            }
            catch
            {
                // Invalid or inaccessible saved paths fall back to Documents.
            }

            try
            {
                Directory.CreateDirectory(defaultFolder);
                return defaultFolder;
            }
            catch
            {
                string fallbackFolder = Path.Combine(SettingsFolder, "Archives");
                Directory.CreateDirectory(fallbackFolder);
                return fallbackFolder;
            }
        }

        internal static void SaveArchiveFolder(string archiveFolder)
        {
            string normalizedFolder = Path.GetFullPath(archiveFolder);
            Directory.CreateDirectory(normalizedFolder);
            Directory.CreateDirectory(SettingsFolder);
            File.WriteAllText(
                SettingsPath,
                JsonSerializer.Serialize(new ArchiveSettings { ArchiveFolder = normalizedFolder }));
        }

        private static string GetDefaultArchiveFolder()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents))
                documents = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(documents, "W系列热泵曲线存档");
        }
    }
}

using System;
using System.IO;
using System.Text.Json;
using SyncDB.Model;

namespace SyncDB.Service
{
    public class ConfigService
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly string _configPath;

        public ConfigService(string appPath)
        {
            _configPath = Path.Combine(appPath, "config.json");
        }

        public AppConfig Load()
        {
            try
            {
                if (!File.Exists(_configPath))
                    return new AppConfig();

                var json = File.ReadAllText(_configPath);
                return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            }
            catch
            {
                return new AppConfig();
            }
        }

        public void Save(AppConfig config)
        {
            try
            {
                var json = JsonSerializer.Serialize(config, JsonOptions);
                File.WriteAllText(_configPath, json);
            }
            catch { }
        }
    }
}

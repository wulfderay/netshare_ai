using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace NetShare.Core.Settings
{
    public sealed class SettingsStore
    {
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();

        public string SettingsPath { get; }

        public SettingsStore(string appName = "NetShare")
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), appName);
            Directory.CreateDirectory(dir);
            SettingsPath = Path.Combine(dir, "settings.json");
        }

        public AppSettings LoadOrCreate()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    var s = new AppSettings();
                    Save(s);
                    return s;
                }
                var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
                var s2 = _serializer.Deserialize<AppSettings>(json);
                if (s2 == null) throw new InvalidOperationException("Failed to parse settings.");
                if (string.IsNullOrWhiteSpace(s2.DeviceId)) s2.DeviceId = Guid.NewGuid().ToString();
                if (string.IsNullOrWhiteSpace(s2.DeviceName)) s2.DeviceName = Environment.MachineName;
                return s2;
            }
            catch
            {
                var s = new AppSettings();
                Save(s);
                return s;
            }
        }

        public void Save(AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var json = _serializer.Serialize(settings);
            File.WriteAllText(SettingsPath, json, Encoding.UTF8);
        }
    }
}

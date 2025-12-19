namespace NetShare.Linux.Core.Settings;

public static class LinuxPaths
{
    public static string ConfigDir()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdg)) return Path.Combine(xdg, "netshare");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "netshare");
    }

    public static string SettingsPath() => Path.Combine(ConfigDir(), "config.json");

    public static string DefaultDownloadDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Downloads", "NetShare");
    }
}

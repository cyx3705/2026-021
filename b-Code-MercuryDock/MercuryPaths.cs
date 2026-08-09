using System.IO;

namespace Mercury;

/// <summary>HistoryMercury runtime locations and read-only migration sources.</summary>
internal static class MercuryPaths
{
    public const string HostName = "HistoryVulcan";
    public const string LegacyHostName = "OneHistoryStudio";
    public const string ModuleName = "HistoryMercury";
    private const string PreviousModuleName = "MercuryDock";

    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static string AppRoot => Path.Combine(AppData, HostName);
    public static string LegacyAppRoot => Path.Combine(AppData, LegacyHostName);
    public static string DataRoot => Path.Combine(AppRoot, ModuleName);
    public static string PreviousDataRoot => Path.Combine(AppRoot, PreviousModuleName);
    public static string LegacyMercuryDockDataRoot => Path.Combine(LegacyAppRoot, PreviousModuleName);
    public static string LegacyActiveDockDataRoot => Path.Combine(LegacyAppRoot, "ActiveDock");
    public static string SettingsPath => Path.Combine(AppRoot, "settings.json");
    public static string LegacySettingsPath => Path.Combine(LegacyAppRoot, "settings.json");
    public static string ModuleSlotRoot => Path.Combine(AppRoot, "Modules", ModuleName);
    public static string PreviousModuleSlotRoot => Path.Combine(AppRoot, "Modules", PreviousModuleName);
    public static string LegacyModuleSlotRoot => Path.Combine(LegacyAppRoot, "Modules", PreviousModuleName);
}

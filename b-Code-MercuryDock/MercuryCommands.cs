using System.Diagnostics;
using HistoryVulcan.Core.Commands;

namespace Mercury;

public static class MercuryCommands
{
    public static ExplorerEntryStatus Status()
        => new(ExplorerNamespaceRegistration.IsRegistered(), DockShortcutFolder.Path);

    public static string RegisterExplorer()
    {
        DockShortcutFolder.Synchronize(MercuryState.Projects);
        return ExplorerNamespaceRegistration.RegisterOrUpdate(DockShortcutFolder.Path).Message;
    }

    public static string RemoveExplorer()
        => ExplorerNamespaceRegistration.RemoveRegistration().Message;

    public static IReadOnlyList<DockProject> ListProjects() => MercuryState.Projects;

    public static string PinProject(string name)
        => MercuryState.Pin(name, pinned: true) ? $"Pinned {name}." : $"Project not found: {name}.";

    public static string UnpinProject(string name)
        => MercuryState.Pin(name, pinned: false) ? $"Unpinned {name}." : $"Project not found: {name}.";

    public static string AddProject(string name)
        => MercuryState.AddToDock(name) ? $"Added and pinned {name}." : $"Project not found: {name}.";

    public static Task<IReadOnlyList<DockProject>> RefreshProjectsAsync()
        => MercuryState.RefreshAsync();

    public static string HideDock()
    {
        MercuryState.SetHidden(true);
        return "Dock hidden.";
    }

    public static string ShowDock()
    {
        MercuryState.SetHidden(false);
        return "Dock shown.";
    }

    public static string OpenHost()
        => HistoryVulcanLauncher.Open() ? "Activated the running HistoryVulcan window." : "Started HistoryVulcan.";

    public static IReadOnlyList<DockUsageRow> ListUsage()
        => MercuryState.Projects
            .Select(item => new DockUsageRow(
                item.Name,
                Math.Round(item.Weight, 3),
                Math.Round(item.Clicks, 3),
                item.LastOpened,
                item.Pinned,
                item.Excluded))
            .ToList();

    public static string ForgetUsage(string? name = null)
    {
        MercuryState.Forget(name);
        return string.IsNullOrWhiteSpace(name) ? "Usage history cleared." : $"Usage history cleared for {name}.";
    }

    public static string ExcludeProject(string name)
    {
        MercuryState.Exclude(name, excluded: true);
        return $"Excluded {name}.";
    }

    public static string IncludeProject(string name)
    {
        MercuryState.Exclude(name, excluded: false);
        return $"Included {name}.";
    }

    public static string SetDockPolicy(int? min = null, int? max = null, double? halflife = null)
    {
        if (min != null || max != null || halflife != null)
            MercuryState.SetPolicy(min, max, halflife);
        var current = MercuryState.Policy;
        return $"Policy: min={current.MinItems}, max={current.MaxItems}, halflife={current.HalfLifeDays}.";
    }

    public static CommandResult OpenProject(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return CommandResult.Fail("Usage: mercury.proj.open <name>.");
        if (!MercuryState.TryResolveWorktreeProject(name, out var projectName, out var path))
            return CommandResult.Fail($"Project not found: {name}.");
        try
        {
            MercuryState.RecordOpen(projectName);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return CommandResult.Ok($"Opened {projectName}.");
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"Could not open {projectName}: {ex.GetType().Name}.");
        }
    }
}

public sealed record DockUsageRow(
    string Name,
    double Weight,
    double Clicks,
    DateTimeOffset? LastOpened,
    bool Pinned,
    bool Excluded);

public sealed record ExplorerEntryStatus(bool Registered, string Path);

using System.Collections;
using System.Text.Json;
using HistoryVulcan.Core.Commands;

namespace Mercury;

internal static class MercuryCommandCatalog
{
    public const string ProjectOpenCommandName = "mercury.proj.open";

    public static void Register(CommandRegistry registry)
    {
        foreach (var command in CreateDescriptors())
            registry.Register(command);
    }

    public static string BuildOpenProjectCommand(string nameOrNumber)
        => ProjectOpenCommandName + " " + CommandParser.QuoteArg(nameOrNumber.Trim());

    public static IReadOnlyList<CommandCatalogItem> FallbackCommandCatalog()
        => CreateDescriptors()
            .Select(command => new CommandCatalogItem(
                command.Name,
                "Mercury",
                command.CommandClass ?? "core",
                command.Summary))
            .ToList();

    public static IReadOnlyList<CommandCatalogItem> ParseCommandCatalog(object? data)
    {
        try
        {
            if (data is JsonElement json && json.ValueKind == JsonValueKind.Array)
            {
                return json.EnumerateArray()
                    .Select(ReadJsonItem)
                    .Where(item => item != null)
                    .Cast<CommandCatalogItem>()
                    .ToList();
            }

            if (data is IEnumerable items)
            {
                return items.Cast<object?>()
                    .Select(ReadTypedItem)
                    .Where(item => item != null)
                    .Cast<CommandCatalogItem>()
                    .ToList();
            }
        }
        catch (Exception)
        {
        }

        return [];
    }

    private static IReadOnlyList<CommandDescriptor> CreateDescriptors() =>
    [
        Readonly("mercury.status", "status", "Show the managed Explorer entry.",
            _ => MercuryCommands.Status()),
        Write("mercury.explorer.register", "explorer", "Register the managed Explorer entry.",
            _ => MercuryCommands.RegisterExplorer()),
        Write("mercury.explorer.remove", "explorer", "Remove the managed Explorer entry.",
            _ => MercuryCommands.RemoveExplorer(), "Remove the managed Explorer entry?"),
        Readonly("mercury.proj.list", "proj", "List active projects.",
            _ => MercuryCommands.ListProjects()),
        Write("mercury.proj.pin", "proj", "Pin an active project.",
            context => MercuryCommands.PinProject(context.RequireString("name")), NameParameter()),
        Write("mercury.proj.unpin", "proj", "Unpin an active project.",
            context => MercuryCommands.UnpinProject(context.RequireString("name")), NameParameter()),
        Write("mercury.proj.add", "proj", "Add and pin a project.",
            context => MercuryCommands.AddProject(context.RequireString("name")), NameParameter()),
        Async("mercury.proj.refresh", "proj", "Rescan active projects.",
            async _ => await MercuryCommands.RefreshProjectsAsync().ConfigureAwait(false)),
        Write("mercury.proj.exclude", "proj", "Exclude a project from the dock.",
            context => MercuryCommands.ExcludeProject(context.RequireString("name")), NameParameter()),
        Write("mercury.proj.include", "proj", "Include a project in the dock.",
            context => MercuryCommands.IncludeProject(context.RequireString("name")), NameParameter()),
        new CommandDescriptor
        {
            Name = ProjectOpenCommandName,
            CommandClass = "proj",
            Summary = "Open a project directory.",
            Example = "mercury.proj.open 2026-021-HistoryMercury",
            Parameters = [NameParameter()],
            Handler = CommandDescriptor.Sync(context => MercuryCommands.OpenProject(context.GetString("name"))),
        },
        Write("mercury.dock.hide", "dock", "Hide the dock.", _ => MercuryCommands.HideDock()),
        Write("mercury.dock.show", "dock", "Show the dock.", _ => MercuryCommands.ShowDock()),
        Write("mercury.dock.policy", "dock", "Read or update dock policy.",
            context => MercuryCommands.SetDockPolicy(
                context.Has("min") ? context.GetInt("min") : null,
                context.Has("max") ? context.GetInt("max") : null,
                context.Has("halflife") ? context.GetDouble("halflife") : null),
            OptionalIntParameter("min"), OptionalIntParameter("max"), OptionalDoubleParameter("halflife")),
        Write("mercury.app.open", "app", "Open HistoryVulcan.", _ => MercuryCommands.OpenHost()),
        Readonly("mercury.usage.list", "usage", "List project usage.", _ => MercuryCommands.ListUsage()),
        Write("mercury.usage.forget", "usage", "Clear project usage history.",
            context => MercuryCommands.ForgetUsage(context.GetString("name")), "Clear all selected usage history?", OptionalNameParameter()),
    ];

    private static CommandDescriptor Readonly(string name, string commandClass, string summary, Func<CommandContext, object?> handler)
        => new()
        {
            Name = name,
            CommandClass = commandClass,
            Summary = summary,
            Readonly = true,
            Handler = CommandDescriptor.Sync(context => CommandResult.Ok(data: handler(context))),
        };

    private static CommandDescriptor Write(
        string name,
        string commandClass,
        string summary,
        Func<CommandContext, string> handler,
        params ParameterSpec[] parameters)
        => Write(name, commandClass, summary, handler, null, parameters);

    private static CommandDescriptor Write(
        string name,
        string commandClass,
        string summary,
        Func<CommandContext, string> handler,
        string? confirm,
        params ParameterSpec[] parameters)
        => new()
        {
            Name = name,
            CommandClass = commandClass,
            Summary = summary,
            Parameters = parameters,
            ConfirmPrompt = confirm == null ? null : _ => confirm,
            Dangerous = confirm != null,
            Handler = CommandDescriptor.Sync(context => CommandResult.Ok(handler(context))),
        };

    private static CommandDescriptor Async(string name, string commandClass, string summary, Func<CommandContext, Task<object?>> handler)
        => new()
        {
            Name = name,
            CommandClass = commandClass,
            Summary = summary,
            Handler = async context => CommandResult.Ok(data: await handler(context).ConfigureAwait(false)),
        };

    private static ParameterSpec NameParameter() => new()
    {
        Name = "name",
        Description = "Project name or number.",
        Required = true,
        Position = 0,
    };

    private static ParameterSpec OptionalNameParameter() => new()
    {
        Name = "name",
        Description = "Project name or number; omit to clear all.",
        Required = false,
        Position = 0,
    };

    private static ParameterSpec OptionalIntParameter(string name) => new()
    {
        Name = name,
        Description = "Optional integer policy value.",
        Type = ParamType.Int,
        Required = false,
    };

    private static ParameterSpec OptionalDoubleParameter(string name) => new()
    {
        Name = name,
        Description = "Optional decimal policy value.",
        Type = ParamType.Double,
        Required = false,
    };

    private static CommandCatalogItem? ReadJsonItem(JsonElement item)
    {
        if (!item.TryGetProperty("commandName", out var name) || string.IsNullOrWhiteSpace(name.GetString()))
            return null;
        return new CommandCatalogItem(
            name.GetString()!,
            ReadJsonString(item, "domain"),
            ReadJsonString(item, "commandClass"),
            ReadJsonString(item, "summary"));
    }

    private static string ReadJsonString(JsonElement item, string property)
        => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static CommandCatalogItem? ReadTypedItem(object? item)
    {
        var type = item?.GetType();
        var name = type?.GetProperty("CommandName")?.GetValue(item) as string;
        if (string.IsNullOrWhiteSpace(name))
            return null;
        return new CommandCatalogItem(
            name,
            type?.GetProperty("Domain")?.GetValue(item) as string ?? string.Empty,
            type?.GetProperty("CommandClass")?.GetValue(item) as string ?? string.Empty,
            type?.GetProperty("Summary")?.GetValue(item) as string ?? string.Empty);
    }
}

public sealed record CommandCatalogItem(string Name, string Domain, string CommandClass, string Summary);

internal sealed class CommandOptionTree
{
    private readonly Node _root = new();

    public static CommandOptionTree Build(IReadOnlyList<CommandCatalogItem> commands)
    {
        var tree = new CommandOptionTree();
        foreach (var command in commands)
        {
            var segments = command.Name.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                continue;
            var node = tree._root;
            foreach (var segment in segments)
                node = node.Children.TryGetValue(segment, out var child)
                    ? child
                    : node.Children[segment] = new Node();
            node.Command = command;
        }
        return tree;
    }

    public IReadOnlyList<SuggestOption> ChildrenOf(string text)
    {
        var lastDot = text.LastIndexOf('.');
        var prefix = lastDot < 0 ? string.Empty : text[..(lastDot + 1)];
        var fragment = lastDot < 0 ? text : text[(lastDot + 1)..];

        var node = _root;
        if (prefix.Length > 0)
        {
            foreach (var segment in prefix.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!node.Children.TryGetValue(segment, out var child))
                    return [];
                node = child;
            }
        }

        return node.Children
            .Where(pair => pair.Key.StartsWith(fragment, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new SuggestOption(
                pair.Value.Children.Count > 0 ? prefix + pair.Key + "." : prefix + pair.Key,
                pair.Value.Children.Count > 0 ? pair.Key + " >" : pair.Key,
                Describe(pair.Value),
                pair.Value.Children.Count > 0))
            .ToList();
    }

    private static string Describe(Node node)
    {
        if (node.Command is { Summary.Length: > 0 } command)
            return node.Children.Count > 0 ? command.Summary + " (has subcommands)" : command.Summary;
        return $"{node.Children.Count} subcommands";
    }

    private sealed class Node
    {
        public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public CommandCatalogItem? Command { get; set; }
    }
}

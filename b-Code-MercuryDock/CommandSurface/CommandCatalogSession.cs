using HistoryVulcan.Core;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.CommandSurface;
using HistoryVulcan.Shell;
using HistoryVulcan.Shell.Mcp;

namespace Mercury.CommandSurface;

/// <summary>
/// Owns the runtime command snapshot shared by the console completion surface and command catalog view.
/// The command bus remains the authority, so local and service-backed hosts use the same data path.
/// </summary>
public sealed class CommandCatalogSession : ICommandCatalogSession
{
    private readonly CommandBus _bus;
    private readonly CommandSelectionState _selection;
    private readonly CommandCompletionEngine _completion = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<string, CommandCatalogDetail> _details =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private List<CommandCatalogRow> _allRows = [];
    private List<CommandCatalogRow> _visibleRows = [];
    private IReadOnlyList<string> _domains = [];
    private CommandCatalogFilter _filter = new();
    private bool _loaded;
    private int _refreshQueued;
    private volatile bool _disposed;

    public CommandCatalogSession(CommandBus bus, CommandSelectionState selection)
    {
        _bus = bus;
        _selection = selection;
        _bus.Registry.Changed += OnRegistryChanged;
    }

    public event EventHandler<CommandCatalogChangedEventArgs>? Changed;

    public IReadOnlyList<CommandCatalogRow> AllRows
    {
        get { lock (_gate) return _allRows.ToList(); }
    }

    public IReadOnlyList<CommandCatalogRow> VisibleRows
    {
        get { lock (_gate) return _visibleRows.ToList(); }
    }

    public IReadOnlyList<string> Domains
    {
        get { lock (_gate) return _domains.ToList(); }
    }

    public IReadOnlyList<string> Classes
    {
        get
        {
            lock (_gate)
            {
                if (_filter.Domain == "全部")
                    return [];
                return ClassesForDomainLocked(_filter.Domain);
            }
        }
    }

    public string? SelectedCommandName => _selection.CurrentCommandName;

    public CommandCatalogFilter CurrentFilter
    {
        get { lock (_gate) return _filter; }
    }

    public bool ContainsCommand(string name)
    {
        lock (_gate)
            return _allRows.Any(row => row.CommandName.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> RefreshAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_loaded && !force)
                    return true;
            }

            var snapshot = await LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot == null)
                return false;

            lock (_gate)
            {
                _allRows = snapshot.Value.Rows;
                _domains = snapshot.Value.Domains;
                _details.Clear();
                _loaded = true;
                ApplyFilterLocked();
            }
        }
        finally
        {
            _refreshGate.Release();
        }

        RaiseChanged(CommandCatalogChangeKind.Snapshot);
        return true;
    }

    public void SetFilter(CommandCatalogFilter filter)
    {
        lock (_gate)
        {
            filter = NormalizeFilterLocked(filter);
            if (_filter == filter)
                return;
            _filter = filter;
            ApplyFilterLocked();
        }
        RaiseChanged(CommandCatalogChangeKind.Filter);
    }

    public bool TrySetDomain(string domain, out IReadOnlyList<string> availableDomains)
    {
        lock (_gate)
        {
            availableDomains = ["全部", .. _domains];
            var requested = string.IsNullOrWhiteSpace(domain) ? "全部" : domain;
            var selected = availableDomains.FirstOrDefault(value =>
                value.Equals(requested, StringComparison.OrdinalIgnoreCase));
            if (selected == null)
                return false;

            var next = NormalizeFilterLocked(_filter with { Domain = selected });
            if (_filter == next)
                return true;
            _filter = next;
            ApplyFilterLocked();
        }
        RaiseChanged(CommandCatalogChangeKind.Filter);
        return true;
    }

    public bool TrySetCommandClass(string commandClass, out IReadOnlyList<string> availableClasses)
    {
        lock (_gate)
        {
            availableClasses = _filter.Domain == "全部"
                ? ["全部"]
                : ["全部", .. ClassesForDomainLocked(_filter.Domain)];
            var requested = string.IsNullOrWhiteSpace(commandClass) ? "全部" : commandClass;
            var selected = availableClasses.FirstOrDefault(value =>
                value.Equals(requested, StringComparison.OrdinalIgnoreCase));
            if (selected == null)
                return false;

            var next = _filter with { CommandClass = selected };
            if (_filter == next)
                return true;
            _filter = next;
            ApplyFilterLocked();
        }
        RaiseChanged(CommandCatalogChangeKind.Filter);
        return true;
    }

    public void SetConsoleQuery(string query)
    {
        lock (_gate)
        {
            var next = _filter with { Query = query ?? "" };
            if (_filter == next)
                return;
            _filter = next;
            ApplyFilterLocked();
        }
        RaiseChanged(CommandCatalogChangeKind.Filter);
    }

    public bool MoveSelection(int direction)
    {
        string? selected;
        lock (_gate)
        {
            if (_visibleRows.Count == 0)
                return false;

            var index = _selection.CurrentCommandName == null
                ? -1
                : _visibleRows.FindIndex(row => row.CommandName.Equals(
                    _selection.CurrentCommandName,
                    StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                index = direction < 0 ? 0 : -1;
            index = (index + direction) % _visibleRows.Count;
            if (index < 0)
                index += _visibleRows.Count;
            selected = _visibleRows[index].CommandName;
        }

        _selection.CurrentCommandName = selected;
        RaiseChanged(CommandCatalogChangeKind.Selection);
        return true;
    }

    public void Select(string? commandName)
    {
        _selection.CurrentCommandName = commandName;
        RaiseChanged(CommandCatalogChangeKind.Selection);
    }

    public async Task<ConsoleCompletionResult> CompleteAsync(
        string text,
        int caretIndex,
        CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        var commandName = CommandCompletionEngine.CommandNameBeforeCurrentToken(text, caretIndex);
        if (commandName != null)
            await EnsureDetailAsync(commandName, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<CommandCompletionDefinition> definitions;
        string focusedDomain;
        lock (_gate)
        {
            // 补全用**全量**命令，不用域筛选后的子集：域聚焦时仍必须能补出其他域的绝对名
            // （否则 mercury.go 这类脱固入口在聚焦状态下就补不出来了）。
            // 聚焦域另行传入，由补全引擎决定候选的先后与是否省略域前缀。
            definitions = _allRows.Select(row =>
            {
                if (_details.TryGetValue(row.CommandName, out var detail))
                    return Definition(detail);
                if (_bus.Registry.TryGet(row.CommandName, out var local))
                    return Definition(local);
                return new CommandCompletionDefinition(row.CommandName, row.Summary, []);
            }).ToList();
            focusedDomain = _filter.Domain;
        }
        return _completion.Complete(text, caretIndex, definitions, focusedDomain);
    }

    public async Task<CommandCatalogDetail?> GetDetailAsync(
        string commandName,
        CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureDetailAsync(commandName, cancellationToken).ConfigureAwait(false);
        lock (_gate)
            return _details.GetValueOrDefault(commandName);
    }

    private async Task EnsureDetailAsync(string commandName, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_details.ContainsKey(commandName))
                return;
        }

        if (_bus.Registry.TryGet(commandName, out var local) && _bus.RemoteExecutor == null)
        {
            lock (_gate)
                _details[commandName] = Detail(local);
            return;
        }

        var result = await _bus.ExecuteAsync(
            $"vulcan.command.show name={CommandParser.QuoteArg(commandName)}",
            "UI",
            cancellationToken).ConfigureAwait(false);
        if (!result.Success
            || !CommandResultData.TryRead<CommandCatalogDetail>(result.Data, out var detail))
            return;

        lock (_gate)
            _details[commandName] = detail;
    }

    private async Task<(List<CommandCatalogRow> Rows, IReadOnlyList<string> Domains)?> LoadSnapshotAsync(
        CancellationToken cancellationToken)
    {
        if (_bus.RemoteExecutor == null && !_bus.Registry.TryGet("vulcan.command.list", out _))
            return LocalSnapshot();

        var listResult = await _bus.ExecuteAsync("vulcan.command.list", "UI", cancellationToken)
            .ConfigureAwait(false);
        if (!listResult.Success
            || !CommandResultData.TryRead<IReadOnlyList<CommandCatalogRow>>(listResult.Data, out var rows))
            return null;

        var domainsResult = await _bus.ExecuteAsync("vulcan.command.domains", "UI", cancellationToken)
            .ConfigureAwait(false);
        if (!domainsResult.Success
            || !CommandResultData.TryRead<IReadOnlyList<CommandDomainInfo>>(domainsResult.Data, out var domains))
            return null;

        return (rows.ToList(), domains.Select(row => row.Domain).ToList());
    }

    private (List<CommandCatalogRow> Rows, IReadOnlyList<string> Domains) LocalSnapshot()
    {
        var rows = _bus.Registry.All().Select(descriptor =>
        {
            var source = _bus.Registry.GetSource(descriptor.Name);
            return new CommandCatalogRow(
                descriptor.Name,
                _bus.Registry.GetDomain(descriptor.Name),
                descriptor.Summary,
                descriptor.Example,
                descriptor.Parameters.Count,
                source,
                null,
                descriptor.IsDangerous,
                descriptor.RequiresUiThread,
                null,
                "hidden",
                false,
                false,
                null,
                0,
                0,
                null)
            {
                CommandClass = _bus.Registry.GetCommandClass(descriptor.Name),
                Method = CommandRegistry.GetMethod(descriptor.Name),
            };
        }).ToList();
        var domains = rows.Select(row => row.Domain)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (rows, domains);
    }

    private void ApplyFilterLocked()
    {
        _filter = NormalizeFilterLocked(_filter);
        IEnumerable<CommandCatalogRow> rows = _allRows;
        var keyword = _filter.Query.Trim();
        if (keyword.Length > 0)
        {
            rows = rows.Where(row =>
                row.CommandName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || row.Summary.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || (row.Example?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (!string.IsNullOrWhiteSpace(_filter.Domain) && _filter.Domain != "全部")
            rows = rows.Where(row => row.Domain.Equals(_filter.Domain, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(_filter.CommandClass) && _filter.CommandClass != "全部")
            rows = rows.Where(row => row.CommandClass.Equals(
                _filter.CommandClass,
                StringComparison.OrdinalIgnoreCase));

        rows = _filter.McpFilter switch
        {
            1 => rows.Where(row => row.PolicyVisible),
            2 => rows.Where(row => !row.PolicyVisible),
            3 => rows.Where(row => row.McpState == "hidden"),
            4 => rows.Where(row => row.McpState == "dangerous"),
            _ => rows,
        };

        _visibleRows = rows.ToList();
        var selected = _selection.CurrentCommandName == null
            ? null
            : _visibleRows.FirstOrDefault(row => row.CommandName.Equals(
                _selection.CurrentCommandName,
                StringComparison.OrdinalIgnoreCase));
        if (selected == null && keyword.Length > 0)
            selected = _visibleRows.FirstOrDefault();
        _selection.CurrentCommandName = selected?.CommandName;
    }

    private CommandCatalogFilter NormalizeFilterLocked(CommandCatalogFilter filter)
    {
        var domain = filter.Domain;
        if (string.IsNullOrWhiteSpace(domain)
            || domain == "全部"
            || !_domains.Contains(domain, StringComparer.OrdinalIgnoreCase))
        {
            return filter with { Domain = "全部", CommandClass = "全部" };
        }

        domain = _domains.First(value => value.Equals(domain, StringComparison.OrdinalIgnoreCase));
        var classes = ClassesForDomainLocked(domain);
        var commandClass = classes.FirstOrDefault(value =>
            value.Equals(filter.CommandClass, StringComparison.OrdinalIgnoreCase)) ?? "全部";
        return filter with { Domain = domain, CommandClass = commandClass };
    }

    private IReadOnlyList<string> ClassesForDomainLocked(string domain)
        => _allRows
            .Where(row => row.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase))
            .Select(row => string.IsNullOrWhiteSpace(row.CommandClass) ? "core" : row.CommandClass)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();

    private IEnumerable<CommandCatalogRow> RowsInCurrentTaxonomyLocked()
    {
        IEnumerable<CommandCatalogRow> rows = _allRows;
        if (_filter.Domain != "全部")
            rows = rows.Where(row => row.Domain.Equals(_filter.Domain, StringComparison.OrdinalIgnoreCase));
        if (_filter.CommandClass != "全部")
            rows = rows.Where(row => row.CommandClass.Equals(
                _filter.CommandClass,
                StringComparison.OrdinalIgnoreCase));
        return rows;
    }

    private void OnRegistryChanged()
    {
        if (_disposed)
            return;
        lock (_gate)
        {
            _loaded = false;
            _details.Clear();
        }
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0)
            return;
        _ = NotifyInvalidatedAsync();
    }

    private async Task NotifyInvalidatedAsync()
    {
        await Task.Delay(200).ConfigureAwait(false);
        Interlocked.Exchange(ref _refreshQueued, 0);
        if (_disposed)
            return;
        RaiseChanged(CommandCatalogChangeKind.Invalidated);
    }

    private static CommandCompletionDefinition Definition(CommandDescriptor descriptor)
        => new(descriptor.Name, descriptor.Summary, descriptor.Parameters);

    private static CommandCompletionDefinition Definition(CommandCatalogDetail detail)
        => new(
            detail.Command.CommandName,
            detail.Command.Summary,
            detail.Parameters.Select(parameter => new ParameterSpec
            {
                Name = parameter.Name,
                Description = parameter.Description,
                Type = Enum.TryParse<ParamType>(parameter.Type, true, out var type)
                    ? type
                    : ParamType.String,
                Required = parameter.Required,
                Default = parameter.Default,
                Position = parameter.Position,
                AllowedValues = parameter.AllowedValues.ToArray(),
            }).ToList());

    private CommandCatalogDetail Detail(CommandDescriptor descriptor)
        => new(
            new CommandCatalogRow(
                descriptor.Name,
                _bus.Registry.GetDomain(descriptor.Name),
                descriptor.Summary,
                descriptor.Example,
                descriptor.Parameters.Count,
                "local",
                null,
                descriptor.IsDangerous,
                descriptor.RequiresUiThread,
                null,
                "hidden",
                false,
                false,
                null,
                0,
                0,
                null)
            {
                CommandClass = _bus.Registry.GetCommandClass(descriptor.Name),
                Method = CommandRegistry.GetMethod(descriptor.Name),
            },
            descriptor.Parameters.Select(parameter => new CommandParameterInfo(
                parameter.Name,
                parameter.Type.ToString().ToLowerInvariant(),
                parameter.Required,
                parameter.Default,
                parameter.Position,
                parameter.AllowedValues ?? [],
                parameter.Description)).ToList(),
            null);

    private void RaiseChanged(CommandCatalogChangeKind kind)
    {
        if (!_disposed)
            Changed?.Invoke(this, new CommandCatalogChangedEventArgs(kind));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _bus.Registry.Changed -= OnRegistryChanged;
    }
}

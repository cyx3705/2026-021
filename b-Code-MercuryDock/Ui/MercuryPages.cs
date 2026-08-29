using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mercury.Ui;

/// <summary>
/// Mercury 的页面描述与动作声明（HistoryAurora 页面注册协议 V1）。
/// </summary>
/// <remarks>
/// 宿主 5.0 删除了停靠 SDK 与 UI 模块生命周期，模块不再有地方去构造 WPF 控件；前端也随之
/// 改成**描述化**：Aurora 主动调 <c>mercury.ui.describe</c> / <c>mercury.ui.actions</c>，
/// 用它自己的组件库把描述渲染出来，再按需调 <c>mercury.ui.data</c> 取数。
///
/// 因此本类只产出 JSON，一行 WPF 都不碰：
/// <list type="bullet">
///   <item>描述里没有任何样式键或颜色。模块只能选语义档位（accent / ghost / danger），
///         长相归 Aurora——这是「模块不得自建组件」在结构上的落地；</item>
///   <item>按钮绑的是**动作 id**，不是指令名。指令改名只改
///         <see cref="ActionsJson"/> 里的一处，按钮那侧一个字不动；</item>
///   <item>表格数据不内联进描述，只声明取数命令。描述每次重拉都整份重建，
///         内联会把整张表跟着搬一遍。</item>
/// </list>
///
/// 三条 <c>ui.*</c> 命令一律带 <c>HiddenReason</c>：它们是界面内部协议，对模型没有意义，
/// 且 <c>ui.data</c> 会产生大量条目。4.x 时代这类排除写在宿主的 MCP 策略里；5.0 之后
/// 远端是否可见只看描述符自己的 <c>HiddenReason</c>，因此必须由模块自己声明。
///
/// <para>
/// <b>5.0.1：整页重排，起因是三条前端规则收紧。</b>本类原先对着 Aurora 1.7 写，
/// 而前端一路走到 1.9.1，中间三条**破坏性**变更的失败形态都不是崩溃：
/// </para>
/// <list type="number">
///   <item><b>Aurora 1.8.14 起 <c>button</c> / <c>input</c> / <c>select</c> 不再是页面节点</b>
///         （连同页面按钮的 <c>invoke</c> / <c>enabledWhen</c>）。小型交互控件只能出现在
///         控制面板里。本类此前在表格下面排了 7 个 <c>type: "button"</c>——它们**全部**
///         被渲染成写着退役原因的牌子，扩展坞管理页的行操作因此整排失效。
///         现在：行操作走表格的 <c>rowActions</c>（一份声明同时给出行内按钮与右键菜单），
///         「刷新」这类不针对某一行的操作放进控制面板；</item>
///   <item><b>Aurora 1.9.0 记账：表格的 <c>view</c> 是空转声明</b>（REQ-UI-054）。
///         <c>filterable</c> / <c>sortable</c> / <c>selection</c> 解析得了、全仓没人读它。
///         留着只会让人以为这张表能筛能排。已删；</item>
///   <item><b>Aurora 1.9.0 起页面不滚，装不下就裁掉</b>（REQ-UI-050）。版面因此从审美问题
///         变成功能问题：顶上那两块竖排面板占掉的高度，就是条目表**看不见的行**。
///         现在收录策略横排成一条工具条，而「加入扩展坞」整个移出版面——
///         它一周用不到一次，却是当时最高的一块。</item>
/// </list>
/// <para>
/// 「加入扩展坞」现在挂在页面右键上（Aurora 1.9.1 的 <c>popup.trigger</c>，REQ-UI-056）。
/// 这条能力是为本页提的：弹出层本来就存在，缺的只是一个不占版面的触发方式，
/// 因此 Aurora 那边没有新建组件，只给现成的浮层多了一条开关。
/// 代价是**看不见**——版面上没有东西提示「这里右键有内容」，所以页首那句说明必须写出来。
/// </para>
/// </remarks>
internal static class MercuryPages
{
    /// <summary>协议版本。文本协议没有编译器保护，两端必须显式比对。</summary>
    private const int SchemaVersion = 1;

    /// <summary>owner 必须与宿主注册表里的模块名逐字符相等，否则 Aurora 整份作废。</summary>
    private const string Owner = "HistoryMercury";

    /// <summary>动作 id 前缀。id 是按钮唯一记住的东西，因此比指令名更需要稳定。</summary>
    private const string ActionPrefix = "mercury.entry.";

    public const string OpenAction = ActionPrefix + "open";
    public const string PinAction = ActionPrefix + "pin";
    public const string UnpinAction = ActionPrefix + "unpin";
    public const string ExcludeAction = ActionPrefix + "exclude";
    public const string IncludeAction = ActionPrefix + "include";
    public const string RemoveAction = ActionPrefix + "remove";
    public const string RefreshAction = ActionPrefix + "refresh";
    public const string AddProjectAction = ActionPrefix + "addproject";
    public const string AddCommandAction = ActionPrefix + "addcommand";
    public const string AddShortcutAction = ActionPrefix + "addshortcut";
    public const string PickShortcutAction = ActionPrefix + "pickshortcut";
    public const string PolicyAction = ActionPrefix + "policy";

    /// <summary>本模块声明的页面 id，供烟测与文档引用。</summary>
    public const string ManagerPageId = "dock.manager";

    public const string CommandsPageId = "mercury.commands";

    /// <summary>条目表的节点 id。行操作按被点的那一行取值，因此它只用于日志归因与烟测。</summary>
    public const string EntriesNodeId = "entries";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>页面描述。策略面板的初值取自当前状态，重拉一次即刷新。</summary>
    public static string DescribeJson()
    {
        var policy = MercuryState.Policy;
        var description = new
        {
            schemaVersion = SchemaVersion,
            owner = Owner,
            pages = new object[]
            {
                new
                {
                    id = ManagerPageId,
                    title = "扩展坞管理",
                    // 不写 ratio：中央文档区不吃比例（比例只用于四边），而 Aurora 会把
                    // 落在 (0,1) 之外的值静默换成 0.25——描述里写 1.0 只会让描述与实际不符。
                    placement = new { side = "center", visible = true, singleton = true },
                    content = new
                    {
                        type = "stack",
                        orientation = "vertical",
                        // tight 而不是 normal：页面不滚（Aurora REQ-UI-050），
                        // 每一档间距都是从条目表身上扣的。
                        gap = "tight",
                        children = new object[]
                        {
                            Text(
                                "扩展坞收录活动项目与常驻指令项：项目按使用权重自动收录，常驻项手动增删。"
                                + "右键页面空白处加入新条目；右键某一行看该条目的全部操作。",
                                "caption"),
                            OpsPanel(policy),
                            EntriesTable(),
                            AddPopup(),
                        },
                    },
                },
                new
                {
                    id = CommandsPageId,
                    title = "命令集",
                    placement = new { side = "center", visible = false, singleton = true },
                    content = new
                    {
                        type = "stack",
                        orientation = "vertical",
                        gap = "tight",
                        children = new object[]
                        {
                            Text("宿主注册表的完整命令目录，数据取自 vulcan.command.list。", "caption"),
                            new
                            {
                                type = "table",
                                id = "commands",
                                dataSource = new
                                {
                                    command = MercuryUiData.DataCommandName,
                                    args = new { view = MercuryUiData.CommandsView },
                                },
                                columns = new object[]
                                {
                                    new { key = "name", title = "命令", width = "260" },
                                    new { key = "domain", title = "域", width = "90" },
                                    new { key = "class", title = "类", width = "90" },
                                    new { key = "summary", title = "说明", width = "*" },
                                },
                            },
                        },
                    },
                },
            },
        };

        return JsonSerializer.Serialize(description, Options);
    }

    /// <summary>动作声明。指令改名只改这里的 <c>command</c>，页面描述与按钮不动。</summary>
    /// <remarks>
    /// 行操作的占位符（<c>{key}</c> / <c>{name}</c> / <c>{command}</c>）由 Aurora 用
    /// **被点那一行的同名列**替换，因此这里的 args 与 <see cref="MercuryUiData.Entries"/>
    /// 产出的列名必须逐字对上。对不上不会报错，只会把一个空值发上总线——
    /// 烟测里有一条专门比这两边。
    /// </remarks>
    public static string ActionsJson()
    {
        var actions = new
        {
            schemaVersion = SchemaVersion,
            owner = Owner,
            actions = new object[]
            {
                Action(OpenAction, "打开", "mercury.dock.run", new { key = "{key}" },
                    "执行选中条目：项目开目录，常驻项走总线。"),
                Action(PinAction, "固定", "mercury.proj.pin", new { name = "{name}" }, "把项目固定在扩展坞里。"),
                Action(UnpinAction, "取消固定", "mercury.proj.unpin", new { name = "{name}" }, null),
                Action(ExcludeAction, "排除", "mercury.proj.exclude", new { name = "{name}" },
                    "把项目从扩展坞排除，不再自动收录。", danger: true),
                Action(IncludeAction, "重新纳入", "mercury.proj.include", new { name = "{name}" }, null),
                Action(RemoveAction, "移除常驻项", "mercury.dock.remove", new { command = "{command}" },
                    "只对常驻指令项有效。", danger: true),
                Action(RefreshAction, "刷新", "mercury.proj.refresh", null, "重新扫描活动项目。"),
                Action(AddProjectAction, "加为项目", "mercury.proj.add", new { name = "{value}" }, null),
                Action(AddCommandAction, "加为常驻指令", "mercury.dock.add", new { command = "{value}" }, null),
                Action(AddShortcutAction, "加为快捷文件", "mercury.shortcut.add", new { path = "{value}" }, null),
                Action(PickShortcutAction, "从文件选择…", "mercury.shortcut.pick", null,
                    "打开文件选择器；选中的文件直接加入扩展坞。"),
                Action(PolicyAction, "应用", "mercury.dock.policy",
                    new { min = "{min}", max = "{max}", halflife = "{halflife}" }, "保存收录策略。"),
            },
        };

        return JsonSerializer.Serialize(actions, Options);
    }

    private static object Action(
        string id,
        string title,
        string command,
        object? args,
        string? summary,
        bool danger = false)
        => new { id, title, command, args, summary, danger };

    private static object Text(string text, string? style = null)
        => new { type = "text", text, style };

    /// <summary>
    /// 页面唯一常驻的控制面板：刷新加收录策略，**一行**工具条。
    /// </summary>
    /// <remarks>
    /// **一行不是审美选择。** Aurora 1.9.0 起页面不滚、装不下就裁掉，这一页顶上每多一行，
    /// 条目表就少一行。五个元素写成一个 <c>rows</c> 项，Aurora 按最窄宽度排；
    /// 只有窄到连最窄宽度都放不下时才折行，而折出来的仍然是同一行
    /// （Aurora 协议 V3 / REQ-UI-060）——换行由它算，模块这边没有也不该有列数可声明。
    ///
    /// 模式取 <c>even</c>：三个数字框是同一组同类输入，等比放大之后仍然一样宽，
    /// 而可变宽度模式会把余量全给最后那个「应用」按钮，把它拉成一条横杠。
    ///
    /// 三个数字框**不再写 <c>required</c>**：那个字段随协议 V3 退役了。它当年是全局的，
    /// 三个框共用同一批按钮，等于把整块面板锁在「三个都填了」上——
    /// 而这三项本来就各有默认值，空着一个不该连「刷新」都点不动。
    /// 参数缺失交给策略指令自己的 Required 去报。
    /// </remarks>
    private static object OpsPanel(DockPolicy policy)
        => new
        {
            type = "panel",
            id = "dock.ops",
            text = "扩展坞",
            rows = new object[]
            {
                new
                {
                    mode = "even",
                    widgets = new object[]
                    {
                        new { kind = "button", text = "刷新", action = RefreshAction },
                        Number("min", "最少显示", policy.MinItems),
                        Number("max", "最多显示", policy.MaxItems),
                        Number("halflife", "半衰期(天)", policy.HalfLifeDays),
                        new { kind = "button", text = "应用", action = PolicyAction },
                    },
                },
            },
        };

    /// <summary>
    /// 一个数字输入格。<c>minWidth</c> 写小一点：这三格里放的是个位到三位数，
    /// 按缺省的 120 排会让工具条在中等宽度下就开始折行，而它们本来只要放得下四个字。
    /// </summary>
    private static object Number(string id, string label, IFormattable value)
        => new
        {
            kind = "textbox",
            id,
            label,
            minWidth = 64,
            value = value.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
        };

    /// <summary>
    /// 加入面板。三个按钮共用同一个输入框，而不是先选类型再输入：
    /// 「当前选的是哪种类型」是一份只存在于界面里的状态，描述化协议里没有地方放它，
    /// 硬塞就得为它新增一条只在 UI 内有意义的命令。
    /// </summary>
    /// <remarks>
    /// <c>trigger: "context"</c>（Aurora 1.9.1 / REQ-UI-056）：整块**不占版面**，
    /// 右键页面空白处才弹出来。它是这一页最高的一块，而实际使用频率是一周一两次——
    /// 页面不滚之后，它占的高度就是条目表看不见的行。
    ///
    /// 内部**一个元素一行**：一个输入框加四个按钮，挤在一行会被浮层宽度压成一团。
    /// 分成五行之后它读起来正是一个右键菜单——顶上一个输入框，下面四条「加为……」。
    /// </remarks>
    private static object AddPopup()
        => new
        {
            type = "popup",
            id = "dock.add",
            text = "加入扩展坞",
            trigger = "context",
            rows = new object[]
            {
                new
                {
                    widgets = new object[]
                    {
                        new { kind = "textbox", id = "value", label = "名称 / 指令 / 路径" },
                    },
                },
                Row("加为项目", AddProjectAction),
                Row("加为常驻指令", AddCommandAction),
                Row("加为快捷文件", AddShortcutAction),
                Row("从文件选择…", PickShortcutAction),
            },
        };

    /// <summary>只放一个按钮的一行。均布，因此按钮铺满浮层宽度，四条读起来像一列菜单项。</summary>
    private static object Row(string text, string action)
        => new
        {
            mode = "even",
            widgets = new object[] { new { kind = "button", text, action } },
        };

    /// <summary>
    /// 条目表。行操作走 <c>rowActions</c>：一份声明同时给出行内按钮与右键菜单。
    /// </summary>
    /// <remarks>
    /// 4.x 到 5.0.0 这排操作是表格下面的一排页面按钮，靠 <c>enabledWhen</c> 跟着选中行走。
    /// Aurora 1.8.14 把页面按钮连同 <c>enabledWhen</c> 一起退役，那一排从此全是警示牌。
    /// 换成行操作之后不再有「选中」这个中间态：**点哪一行就是哪一行**，
    /// 占位符也直接取那一行的同名列，不用绕 <c>entries.selected.name</c> 那种跨节点路径。
    ///
    /// 只有「打开」「固定」留在行内。Aurora 的用法文档明说行操作超过两三条就该用
    /// <c>inline: false</c>——六个按钮会把数据列挤没，而那正是这个组件要解决的问题的反面。
    /// 其余四条只进右键菜单：它们要么低频（排除 / 重新纳入 / 取消固定），
    /// 要么只对一类行有意义（移除常驻项）。
    ///
    /// 没有 <c>view</c>：<c>filterable</c> / <c>sortable</c> / <c>selection</c> 在 Aurora
    /// 里解析得了但全仓没人读（REQ-UI-054）。留着只会让人以为这张表能筛能排。
    /// </remarks>
    private static object EntriesTable()
        => new
        {
            type = "table",
            id = EntriesNodeId,
            dataSource = new
            {
                command = MercuryUiData.DataCommandName,
                args = new { view = MercuryUiData.EntriesView },
            },
            columns = new object[]
            {
                new { key = "name", title = "名称", width = "180" },
                new { key = "type", title = "类型", width = "70" },
                new { key = "state", title = "状态", width = "74" },
                new { key = "command", title = "指令", width = "*" },
                new { key = "weight", title = "权重", width = "62" },
                new { key = "clicks", title = "点击", width = "54" },
                new { key = "lastopened", title = "最近打开", width = "128" },
            },
            rowActions = new object[]
            {
                RowAction(OpenAction, "打开"),
                RowAction(PinAction, "固定"),
                RowAction(UnpinAction, "取消固定", inline: false),
                RowAction(ExcludeAction, "排除", inline: false, style: "danger"),
                RowAction(IncludeAction, "重新纳入", inline: false),
                RowAction(RemoveAction, "移除常驻项", inline: false, style: "danger"),
            },
        };

    /// <summary>一条行操作。<c>inline</c> 缺省 true：不写就同时进行内按钮与右键菜单。</summary>
    private static object RowAction(string action, string title, bool inline = true, string? style = null)
        => new { action, title, inline, style };
}

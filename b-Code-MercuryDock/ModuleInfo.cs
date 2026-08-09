using BaseVariable;

namespace Mercury;

public sealed class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "HistoryMercury";
    public string CommandPrefix => "mercury";
    public override string Description => "活动项目坞与资源管理器 HistoryVesta 项目入口";
    public override string Author => "OneHistory";
    public override string Version => "4.1.0";
    // Commands are registered once through IModuleContext with their full three-part names.
    public override Type? MainClassType => null;
}

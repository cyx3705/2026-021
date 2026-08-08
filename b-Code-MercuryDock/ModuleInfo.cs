using BaseVariable;

namespace MercuryDock;

public sealed class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "MercuryDock";
    public string CommandPrefix => "dock";
    public override string Description => "活动项目坞与资源管理器 OHS 项目入口";
    public override string Author => "OneHistory";
    public override string Version => "3.2.3";
    public override Type? MainClassType => typeof(MercuryDockCommands);
}

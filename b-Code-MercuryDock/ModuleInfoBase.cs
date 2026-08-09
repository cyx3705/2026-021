namespace BaseVariable;

/// <summary>HistoryVulcan 模块识别合同的兼容副本。</summary>
public abstract class ModuleInfoBase
{
    public virtual string ModuleName => GetType().Assembly.GetName().Name ?? "UnknownModule";
    public virtual string Description => "";
    public virtual string Author => "";
    public virtual string Version => "v1.0.0";
    public virtual bool Open => false;
    public virtual Type? MainClassType => null;
    public virtual bool Enabled => true;
}

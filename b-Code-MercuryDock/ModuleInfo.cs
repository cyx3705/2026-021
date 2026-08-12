using System.Reflection;
using BaseVariable;

namespace Mercury;

public sealed class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "HistoryMercury";
    public string CommandPrefix => "mercury";
    public override string Description => "活动项目坞、快捷键与命令工作台";
    public override string Author => "OneHistory";

    /// <summary>
    /// 版本取自程序集，不写字面量。宿主装载时比对 manifest 与本类的版本，两者不一致会
    /// **静默跳过整个模块**——只在服务进程日志留一行 module.discovery 警告，界面上表现为
    /// 活动坞与命令集页一起消失。4.2.0 就因为漏改这里的字面量复现过一次，故收敛为单一来源。
    /// </summary>
    public override string Version { get; } = ReadAssemblyVersion();

    // Commands are registered once through IModuleContext with their full three-part names.
    public override Type? MainClassType => null;

    /// <summary>
    /// 取 InformationalVersion 并去掉 <c>+源码修订号</c> 后缀；缺失时回退到程序集版本的三段形式。
    /// manifest 里写的是 <c>4.2.0</c> 这种三段语义版本，两边必须能逐字符相等。
    /// </summary>
    private static string ReadAssemblyVersion()
    {
        var assembly = typeof(ModuleInfo).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}

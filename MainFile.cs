using QuickSL.Core;
using Godot;
using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Modding;

namespace QuickSL;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public static void Initialize()
    {
        JmcModLib.Core.ModRegistry.Register(true, VersionInfo.Name, VersionInfo.Name, VersionInfo.Version)?
            .RegisterLogger(uIFlags: LogConfigUIFlags.All)
            .UseConfig()
            .Done();

        ModLogger.Info("======================================");
        ModLogger.Info($" {VersionInfo.Name} v{VersionInfo.Version} 正在启动...");
        ModLogger.Info("======================================");
    }
}

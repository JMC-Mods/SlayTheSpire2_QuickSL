using QuickSL.Core;
using Godot;
using HarmonyLib;
using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Modding;
using System.Reflection;

namespace QuickSL;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public static void Initialize()
    {
        var registry = JmcModLib.Core.ModRegistry.Register(true, VersionInfo.Name, VersionInfo.Name, VersionInfo.Version)?
            .RegisterLogger(uIFlags: LogConfigUIFlags.All);

        registry?
            .UseConfig()
            .Done();

        new Harmony($"JMC.{VersionInfo.Name}").PatchAll(Assembly.GetExecutingAssembly());

        ModLogger.Info("======================================");
        ModLogger.Info($" {VersionInfo.Name} v{VersionInfo.Version} 正在启动...");
        ModLogger.Info("======================================");
    }
}

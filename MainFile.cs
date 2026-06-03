using Godot;
using HarmonyLib;
using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Modding;
using QuickSL.Core;
using System.Reflection;

namespace QuickSL;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public static void Initialize()
    {
        JmcModLib.Core.ModRegistry.Register<MainFile>();

        new Harmony($"JMC.{VersionInfo.Name}").PatchAll(Assembly.GetExecutingAssembly());

        ModLogger.Info("======================================");
        ModLogger.Info($" {VersionInfo.Name} v{VersionInfo.Version} 正在启动...");
        ModLogger.Info("======================================");
    }
}

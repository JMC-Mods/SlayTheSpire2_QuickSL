using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;

namespace QuickSL.Core;

[HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
internal static class RunManagerLaunchPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        MultiplayerQuickSlCoordinator.EnsureHandlersRegistered();
    }
}

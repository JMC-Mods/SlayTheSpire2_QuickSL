using Godot;
using HarmonyLib;
using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Commands;
using System.Threading;
using System.Threading.Tasks;

namespace QuickSL.Core;

internal static class QuickSlAsyncOperationGuard
{
    private static CancellationTokenSource currentReloadGeneration = new();

    public static void CancelPendingGameWaits()
    {
        CancellationTokenSource oldGeneration =
            Interlocked.Exchange(ref currentReloadGeneration, new CancellationTokenSource());

        try
        {
            oldGeneration.Cancel();
            ModLogger.Debug("快速 SL：已取消旧局残留的等待/动画结算。");
        }
        finally
        {
            oldGeneration.Dispose();
        }
    }

    public static Task WaitWithQuickSlCancellation(SceneTreeTimer timer, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var syncRoot = new object();
        CancellationTokenRegistration gameRegistration = default;
        CancellationTokenRegistration quickSlRegistration = default;
        bool cleanedUp = false;

        void Complete()
        {
            if (completion.TrySetResult())
            {
                CleanUp();
            }
        }

        void Cancel(CancellationToken token)
        {
            if (completion.TrySetCanceled(token))
            {
                CleanUp();
            }
        }

        void CleanUp()
        {
            lock (syncRoot)
            {
                if (cleanedUp)
                {
                    return;
                }

                cleanedUp = true;
                timer.Timeout -= Complete;
                gameRegistration.Dispose();
                quickSlRegistration.Dispose();
            }
        }

        timer.Timeout += Complete;
        Register(cancellationToken, ref gameRegistration);
        Register(Volatile.Read(ref currentReloadGeneration).Token, ref quickSlRegistration);

        return completion.Task;

        void Register(CancellationToken token, ref CancellationTokenRegistration registration)
        {
            if (!token.CanBeCanceled)
            {
                return;
            }

            CancellationTokenRegistration newRegistration = token.Register(() => Cancel(token));
            lock (syncRoot)
            {
                if (cleanedUp)
                {
                    newRegistration.Dispose();
                }
                else
                {
                    registration = newRegistration;
                }
            }
        }
    }
}

[HarmonyPatch(typeof(Cmd), "WaitInternal")]
internal static class CmdWaitInternalPatch
{
    [HarmonyPrefix]
    private static bool Prefix(SceneTreeTimer timer, CancellationToken cancellationToken, ref Task __result)
    {
        __result = QuickSlAsyncOperationGuard.WaitWithQuickSlCancellation(timer, cancellationToken);
        return false;
    }
}

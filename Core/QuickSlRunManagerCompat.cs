using JmcModLib.Reflection;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using System.Runtime.ExceptionServices;

namespace QuickSL.Core;

internal static class QuickSlRunManagerCompat
{
    public static Task SetUpSavedSinglePlayerAsync(
        RunManager runManager,
        RunState runState,
        SerializableRun runSave)
    {
        MethodAccessor method = MethodAccessor.Get(
            typeof(RunManager),
            nameof(RunManager.SetUpSavedSinglePlayer),
            [typeof(RunState), typeof(SerializableRun)]);

        return InvokeMaybeAsync(
            method,
            Sts2GameVersionCompat.UsesAsyncSavedRunSetup,
            runManager,
            runState,
            runSave);
    }

    public static Task SetUpSavedMultiPlayerAsync(
        RunManager runManager,
        RunState runState,
        LoadRunLobby loadLobby)
    {
        MethodAccessor method = MethodAccessor.Get(
            typeof(RunManager),
            nameof(RunManager.SetUpSavedMultiPlayer),
            [typeof(RunState), typeof(LoadRunLobby)]);

        return InvokeMaybeAsync(
            method,
            Sts2GameVersionCompat.UsesAsyncSavedRunSetup,
            runManager,
            runState,
            loadLobby);
    }

    private static async Task InvokeMaybeAsync(
        MethodAccessor method,
        bool methodShouldReturnTask,
        object target,
        params object[] args)
    {
        object? result;
        try
        {
            result = method.Invoke(target, args);
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }

        // STS2 0.105 起 RunManager.SetUpSavedSinglePlayer/SetUpSavedMultiPlayer
        // 从 void 改为 Task；旧版返回 void，新版必须等待其异步存档重载计数逻辑完成。
        if (methodShouldReturnTask && result is Task setupTask)
        {
            await setupTask.ConfigureAwait(false);
            return;
        }

        if (result is Task unexpectedTask)
        {
            await unexpectedTask.ConfigureAwait(false);
        }
    }
}

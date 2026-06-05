using JmcModLib.Reflection;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using System.Runtime.ExceptionServices;

namespace QuickSL.Core;

internal static class QuickSlRunManagerCompat
{
    private static readonly string[] SavedSingleplayerMethodNames =
    [
        "SetUpSavedSingleplayer",
        "SetUpSavedSinglePlayer"
    ];

    private static readonly string[] SavedMultiplayerMethodNames =
    [
        "SetUpSavedMultiplayer",
        "SetUpSavedMultiPlayer"
    ];

    public static Task SetUpSavedSinglePlayerAsync(
        RunManager runManager,
        RunState runState,
        SerializableRun runSave)
    {
        MethodAccessor method = GetRunManagerMethod(
            SavedSingleplayerMethodNames,
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
        MethodAccessor method = GetRunManagerMethod(
            SavedMultiplayerMethodNames,
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

        // STS2 0.105 起保存加载入口从 void 改为 Task；新版必须等待异步存档重载计数逻辑完成。
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

    private static MethodAccessor GetRunManagerMethod(
        IEnumerable<string> methodNames,
        Type[] parameterTypes)
    {
        foreach (string methodName in methodNames)
        {
            try
            {
                return MethodAccessor.Get(typeof(RunManager), methodName, parameterTypes);
            }
            catch (MissingMethodException)
            {
                // 兼容 STS2 0.106 及更早的 Player 拼写，以及 0.107 起的新拼写。
            }
        }

        throw new MissingMethodException(
            $"在 {typeof(RunManager).FullName} 找不到兼容的保存加载入口：{string.Join(", ", methodNames)}");
    }
}

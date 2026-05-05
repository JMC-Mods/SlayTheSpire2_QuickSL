using Godot;
using JmcModLib.Prefabs;
using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Platform;
using QuickSL.Core;
using System.Globalization;

namespace QuickSL.UI;

internal static class QuickSlPopupService
{
    private const string Table = "main_menu_ui";
    private const string KeyPrefix = "EXTENSION.QUICKSL.";
    private const string PlayerColor = "#f0b400";

    private static readonly Dictionary<uint, WaitingPopupHandle> WaitingPopups = [];

    public static Task<bool> ShowHostInitiateConfirmationAsync(ulong requesterPlayerId, PlatformType platform)
    {
        return ShowConfirmationAsync(
            "MULTIPLAYER_HOST_CONFIRM.title",
            Format(
                "MULTIPLAYER_HOST_CONFIRM.body",
                ("player", FormatPlayerName(requesterPlayerId, platform))),
            "MULTIPLAYER_HOST_CONFIRM.confirm",
            "MULTIPLAYER_HOST_CONFIRM.cancel");
    }

    public static Task<bool> ShowClientConfirmationAsync(ulong initiatorPlayerId, PlatformType platform)
    {
        string body = initiatorPlayerId == 0
            ? Text("MULTIPLAYER_CONFIRM.body")
            : Format(
                "MULTIPLAYER_CONFIRM_FROM_CLIENT.body",
                ("player", FormatPlayerName(initiatorPlayerId, platform)));

        return ShowConfirmationAsync(
            "MULTIPLAYER_CONFIRM.title",
            body,
            "MULTIPLAYER_CONFIRM.confirm",
            "MULTIPLAYER_CONFIRM.cancel");
    }

    public static void ShowWaitingForHost(uint clientRequestId)
    {
        ShowWaiting(
            clientRequestId,
            "MULTIPLAYER_WAITING_HOST.title",
            "MULTIPLAYER_WAITING_HOST.body",
            "MULTIPLAYER_WAITING_HOST.ok");
    }

    public static void ShowWaitingForPlayers(uint requestId)
    {
        ShowWaiting(
            requestId,
            "MULTIPLAYER_WAITING.title",
            "MULTIPLAYER_WAITING.body",
            "MULTIPLAYER_WAITING.ok");
    }

    public static void CloseWaiting(uint requestId)
    {
        if (!WaitingPopups.Remove(requestId, out WaitingPopupHandle? handle))
        {
            return;
        }

        handle.Close();
    }

    public static void CloseAllWaiting()
    {
        WaitingPopupHandle[] handles = [.. WaitingPopups.Values];
        WaitingPopups.Clear();

        foreach (WaitingPopupHandle handle in handles)
        {
            handle.Close();
        }
    }

    public static async Task ShowRejectedByPlayerAsync(uint requestId, ulong rejectedPlayerId, PlatformType platform)
    {
        CloseWaiting(requestId);
        await ShowMessageAsync(
            "MULTIPLAYER_REJECTED.title",
            Format(
                "MULTIPLAYER_REJECTED.body",
                ("player", FormatPlayerName(rejectedPlayerId, platform))),
            "MULTIPLAYER_REJECTED.ok");
    }

    public static async Task ShowHostRejectedClientInitiationAsync(uint clientRequestId)
    {
        CloseWaiting(clientRequestId);
        await ShowMessageAsync(
            "MULTIPLAYER_HOST_REJECTED.title",
            Text("MULTIPLAYER_HOST_REJECTED.body"),
            "MULTIPLAYER_HOST_REJECTED.ok");
    }

    public static async Task ShowClientInitiationDisabledAsync(uint clientRequestId)
    {
        CloseWaiting(clientRequestId);
        await ShowMessageAsync(
            "MULTIPLAYER_CLIENT_DISABLED.title",
            Text("MULTIPLAYER_CLIENT_DISABLED.body"),
            "MULTIPLAYER_CLIENT_DISABLED.ok");
    }

    public static async Task ShowQuickSlCanceledAsync(uint requestId)
    {
        CloseWaiting(requestId);
        await ShowMessageAsync(
            "MULTIPLAYER_CANCELLED.title",
            Text("MULTIPLAYER_CANCELLED.body"),
            "MULTIPLAYER_CANCELLED.ok");
    }

    private static async Task<bool> ShowConfirmationAsync(
        string titleKey,
        string body,
        string confirmKey,
        string cancelKey)
    {
        try
        {
            if (!JmcConfirmationPopup.IsAvailable)
            {
                ModLogger.Warn("多人快速 SL：弹窗不可用，无法显示确认框。");
                return false;
            }

            return await JmcConfirmationPopup.ShowConfirmationAsync(
                Text(titleKey),
                body,
                Text(confirmKey),
                Text(cancelKey));
        }
        catch (Exception ex)
        {
            ModLogger.Error("多人快速 SL：显示确认框失败。", ex);
            return false;
        }
    }

    private static async Task ShowMessageAsync(string titleKey, string body, string okKey)
    {
        try
        {
            if (!JmcConfirmationPopup.IsAvailable)
            {
                ModLogger.Warn("多人快速 SL：弹窗不可用，无法显示提示框。");
                return;
            }

            await JmcConfirmationPopup.ShowMessageAsync(
                Text(titleKey),
                body,
                Text(okKey));
        }
        catch (Exception ex)
        {
            ModLogger.Error("多人快速 SL：显示提示框失败。", ex);
        }
    }

    private static void ShowWaiting(uint requestId, string titleKey, string bodyKey, string okKey)
    {
        try
        {
            CloseWaiting(requestId);

            if (NModalContainer.Instance is not { OpenModal: null } modalContainer)
            {
                ModLogger.Warn("多人快速 SL：已有原生弹窗打开，跳过等待提示。");
                return;
            }

            NGenericPopup? popup = NGenericPopup.Create();
            if (popup == null)
            {
                ModLogger.Warn("多人快速 SL：无法创建原生等待提示框。");
                return;
            }

            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            popup.Connect(Node.SignalName.TreeExiting, Callable.From(() => closed.TrySetResult()));

            modalContainer.Add(popup);
            if (!ReferenceEquals(modalContainer.OpenModal, popup))
            {
                popup.QueueFree();
                ModLogger.Warn("多人快速 SL：等待提示框被其他弹窗抢占，已跳过。");
                return;
            }

            NVerticalPopup verticalPopup = popup.GetNode<NVerticalPopup>("VerticalPopup");
            verticalPopup.SetText(Text(titleKey), Text(bodyKey));
            verticalPopup.InitYesButton(Loc(okKey), _ => closed.TrySetResult());
            verticalPopup.HideNoButton();

            WaitingPopups[requestId] = new WaitingPopupHandle(popup, closed.Task);
        }
        catch (Exception ex)
        {
            ModLogger.Error("多人快速 SL：显示等待提示框失败。", ex);
        }
    }

    private static LocString Loc(string key)
    {
        return new LocString(Table, KeyPrefix + key);
    }

    private static string Text(string key)
    {
        return Loc(key).GetFormattedText();
    }

    private static string Format(string key, params (string Name, string Value)[] variables)
    {
        LocString loc = Loc(key);
        foreach ((string name, string value) in variables)
        {
            loc.Add(name, value);
        }

        return loc.GetFormattedText();
    }

    private static string FormatPlayerName(ulong playerId, PlatformType platform)
    {
        string playerName = playerId == 0
            ? Text("MULTIPLAYER_PLAYER_UNKNOWN")
            : GetPlayerName(playerId, platform);

        return $"[color={PlayerColor}]{SanitizeRichText(playerName)}[/color]";
    }

    private static string GetPlayerName(ulong playerId, PlatformType platform)
    {
        try
        {
            string playerName = PlatformUtil.GetPlayerName(platform, playerId);
            return string.IsNullOrWhiteSpace(playerName)
                ? playerId.ToString(CultureInfo.InvariantCulture)
                : playerName.Trim();
        }
        catch (Exception ex)
        {
            ModLogger.Debug($"多人快速 SL：获取玩家名称失败，PlayerId={playerId}，错误={ex.Message}。");
            return playerId.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string SanitizeRichText(string text)
    {
        return text
            .Replace('[', '(')
            .Replace(']', ')')
            .ReplaceLineEndings(" ");
    }

    private sealed class WaitingPopupHandle(NGenericPopup popup, Task closedTask)
    {
        private bool closed;

        public void Close()
        {
            if (closed || closedTask.IsCompleted)
            {
                return;
            }

            closed = true;

            try
            {
                if (NModalContainer.Instance is { } modalContainer &&
                    ReferenceEquals(modalContainer.OpenModal, popup))
                {
                    modalContainer.Clear();
                    return;
                }

                if (popup.IsInsideTree())
                {
                    popup.QueueFree();
                }
            }
            catch (Exception ex)
            {
                ModLogger.Debug($"多人快速 SL：关闭等待提示框时出现异常：{ex.Message}");
            }
        }
    }
}

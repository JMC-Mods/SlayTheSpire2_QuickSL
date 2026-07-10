namespace QuickSL.Core;

internal static class MultiplayerQuickSlCoordinator
{
    private static readonly QuickSlMultiplayerController Controller = new();

    public static void EnsureHandlersRegistered()
    {
        Controller.EnsureHandlersRegistered();
    }

    public static Task RunHostAsync()
    {
        return Controller.RunHostAsync();
    }

    public static Task RunClientAsync()
    {
        return Controller.RunClientAsync();
    }

    internal static void HandleFeatureStateChanged(bool enabled)
    {
        Controller.HandleFeatureStateChanged(enabled);
    }
}

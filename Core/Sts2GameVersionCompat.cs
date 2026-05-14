using MegaCrit.Sts2.Core.Debug;

namespace QuickSL.Core;

internal static class Sts2GameVersionCompat
{
    private static readonly Lazy<Sts2GameVersion?> CurrentVersionValue = new(ParseCurrentVersion);

    public static Sts2GameVersion? CurrentVersion => CurrentVersionValue.Value;

    public static bool IsVersionKnown => CurrentVersion.HasValue;

    public static bool SupportsNetMessageBuffering => IsUnknownOrAtLeast(0, 105, 0);

    public static bool UsesAsyncSavedRunSetup => IsUnknownOrAtLeast(0, 105, 0);

    private static bool IsUnknownOrAtLeast(int major, int minor, int patch)
    {
        Sts2GameVersion? currentVersion = CurrentVersion;
        return !currentVersion.HasValue || currentVersion.Value.CompareTo(major, minor, patch) >= 0;
    }

    private static Sts2GameVersion? ParseCurrentVersion()
    {
        string? versionText = ReleaseInfoManager.Instance.ReleaseInfo?.Version;
        if (string.IsNullOrWhiteSpace(versionText))
        {
            return null;
        }

        string normalized = versionText.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        int suffixStart = normalized.IndexOfAny(['-', '+']);
        if (suffixStart >= 0)
        {
            normalized = normalized[..suffixStart];
        }

        string[] parts = normalized.Split('.');
        if (parts.Length < 3 ||
            !int.TryParse(parts[0], out int major) ||
            !int.TryParse(parts[1], out int minor) ||
            !int.TryParse(parts[2], out int patch))
        {
            return null;
        }

        return new Sts2GameVersion(major, minor, patch);
    }
}

internal readonly record struct Sts2GameVersion(int Major, int Minor, int Patch)
{
    public int CompareTo(int major, int minor, int patch)
    {
        int majorComparison = Major.CompareTo(major);
        if (majorComparison != 0)
        {
            return majorComparison;
        }

        int minorComparison = Minor.CompareTo(minor);
        return minorComparison != 0
            ? minorComparison
            : Patch.CompareTo(patch);
    }
}

using MegaCrit.Sts2.Core.Debug;

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

// Compares the running game's version against a given release. For branch-differing
// balance values that only exist as inlined private consts, this is the sanctioned
// alternative to feature detection (see CLAUDE.md "Dual game-branch support"):
// `ReleaseInfoManager.Instance.SemVer` and `SemanticVersion` are public on both branches.
public static class GameVersionHelper
{
    // Returns <0 if the running game is older than major.minor.patch, 0 if it is exactly that
    // version, and >0 if it is newer. A build with no release info (e.g. a local dev build) is
    // treated as newest, so callers fall through to the latest-branch behavior.
    public static int CompareTo(int major, int minor, int patch)
    {
        return ReleaseInfoManager.Instance.SemVer is { } version
            ? version.CompareTo(new SemanticVersion(major, minor, patch))
            : 1;
    }
}

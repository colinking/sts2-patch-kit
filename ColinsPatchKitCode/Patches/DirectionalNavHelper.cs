using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

// v0.110.0 renamed NControllerManager.IsUsingController to IsUsingDirectionalNavigation (now
// also true in the new keyboard-only navigation mode — the same "no mouse cursor" semantic
// every call site here cares about). One shipped dll runs on both game branches, so the
// property is resolved by name with a fallback instead of a compile-time reference. The
// MouseDetected/ControllerDetected signals both still exist and v0.110.0 fires
// ControllerDetected on entering keyboard-only mode, so signal wiring needs no equivalent shim.
public static class DirectionalNavHelper
{
    private static readonly PropertyInfo? _prop =
        AccessTools.Property(typeof(NControllerManager), "IsUsingDirectionalNavigation")
        ?? AccessTools.Property(typeof(NControllerManager), "IsUsingController");

    // Null when no controller manager exists yet, mirroring the original
    // NControllerManager.Instance?.IsUsingController call sites.
    public static bool? IsUsingDirectionalNav =>
        NControllerManager.Instance is { } manager && _prop != null
            ? (bool)_prop.GetValue(manager)!
            : null;
}

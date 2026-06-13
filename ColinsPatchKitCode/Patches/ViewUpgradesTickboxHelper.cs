using System;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

// Shared builder for the "View Upgrades" tickbox added by CardRewardUpgradeTogglePatch and
// BundleUpgradeTogglePatch. No tickbox+label scene ships standalone; every vanilla screen
// composes one in its own .tscn as an NTickbox-scripted container holding a tickbox.tscn
// instance named %TickboxVisuals plus a gold Kreon-bold label (see
// deck_upgrade_select_screen.tscn). This recreates that arrangement in code, reusing the
// game's localized VIEW_UPGRADES string for the label.
public static class ViewUpgradesTickboxHelper
{
    public const string TickboxName = "CpkViewUpgradesTickbox";

    private const string TickboxVisualsScenePath = "res://scenes/ui/tickbox.tscn";
    private const string LabelFontPath = "res://themes/kreon_bold_glyph_space_one.tres";

    private const float BoxSize = 64f;
    private const float BoxLabelGap = 6f;

    // Builds the tickbox in the deck view's bottom-left ViewUpgrades spot (offsets 16/-76 at
    // 0.75 scale, left-middle pivot), adds it to parent, and wires onToggleChanged to both
    // user toggles and mouse<->controller switches: like the deck screens' toggles it is
    // mouse-only, so a controller switch hides it and clears any active preview (which also
    // keeps it clear of controller-only prompts sharing the corner). parent must be inside
    // the tree and ready, so the tickbox readies (and binds its visuals) synchronously.
    public static NTickbox AddTo(Control parent, Action<bool> onToggleChanged)
    {
        NTickbox tickbox = new() { Name = TickboxName };
        Control visuals = ResourceLoader.Load<PackedScene>(TickboxVisualsScenePath)
            .Instantiate<Control>(PackedScene.GenEditState.Disabled);
        tickbox.AddChild(visuals);
        // NTickbox.ConnectSignals finds its visuals via the %TickboxVisuals unique name, which
        // only resolves after the node is registered as unique in the tickbox's owner registry.
        visuals.Owner = tickbox;
        visuals.UniqueNameInOwner = true;
        visuals.Position = Vector2.Zero;
        visuals.Size = new Vector2(BoxSize, BoxSize);

        // Styling copied from the ViewUpgradesLabel nodes in the deck screens' scenes.
        Label label = new()
        {
            Name = "ViewUpgradesLabel",
            Text = new LocString("card_selection", "VIEW_UPGRADES").GetFormattedText(),
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeColorOverride("font_color", new Color(0.937255f, 0.784314f, 0.317647f));
        label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.501961f));
        label.AddThemeConstantOverride("outline_size", 12);
        if (ResourceLoader.Load<Font>(LabelFontPath) is { } labelFont)
        {
            label.AddThemeFontOverride("font", labelFont);
        }
        label.AddThemeFontSizeOverride("font_size", 27);
        tickbox.AddChild(label);

        tickbox.AnchorLeft = 0f;
        tickbox.AnchorTop = 1f;
        tickbox.AnchorRight = 0f;
        tickbox.AnchorBottom = 1f;
        tickbox.OffsetLeft = 16f;
        // Provisional width (the vanilla ViewUpgrades container's 262px); replaced below once
        // the label can be measured in-tree.
        tickbox.OffsetRight = 278f;
        tickbox.OffsetTop = -76f;
        tickbox.OffsetBottom = -12f;
        tickbox.GrowHorizontal = Control.GrowDirection.End;
        tickbox.GrowVertical = Control.GrowDirection.Begin;
        tickbox.Scale = new Vector2(0.75f, 0.75f);
        tickbox.PivotOffset = new Vector2(0f, BoxSize / 2f);

        parent.AddChildSafely(tickbox);

        // Size the label and the clickable rect from an in-tree measurement: inside the tree
        // the label measures with the same fully resolved theme it renders with. A detached
        // measure can come out narrower (theme/font resolution differs without a parent), and
        // since NClickableControl only receives input inside the control rect, that left the
        // right part of the drawn text unclickable. The 6px right padding matches the vanilla
        // ViewUpgrades MarginContainer's margin.
        float labelWidth = label.GetMinimumSize().X;
        label.Position = new Vector2(BoxSize + BoxLabelGap, 0f);
        label.Size = new Vector2(labelWidth, BoxSize);
        tickbox.OffsetRight = tickbox.OffsetLeft + BoxSize + BoxLabelGap + labelWidth + 6f;

        tickbox.IsTicked = false;
        tickbox.Visible = NControllerManager.Instance?.IsUsingController != true;
        tickbox.Connect(NTickbox.SignalName.Toggled,
            Callable.From<NTickbox>(t => onToggleChanged(t.IsTicked)));
        ConnectControllerVisibility(tickbox, onToggleChanged);
        return tickbox;
    }

    private static void ConnectControllerVisibility(NTickbox tickbox, Action<bool> onToggleChanged)
    {
        NControllerManager? manager = NControllerManager.Instance;
        if (manager == null)
        {
            return;
        }
        Callable onDeviceChanged = Callable.From(() =>
        {
            bool usingController = NControllerManager.Instance?.IsUsingController == true;
            tickbox.Visible = !usingController;
            if (usingController && tickbox.IsTicked)
            {
                // The IsTicked setter only updates the tickbox art (no Toggled signal), so the
                // preview must be cleared explicitly.
                tickbox.IsTicked = false;
                onToggleChanged(false);
            }
        });
        manager.Connect(NControllerManager.SignalName.MouseDetected, onDeviceChanged);
        manager.Connect(NControllerManager.SignalName.ControllerDetected, onDeviceChanged);
        // The connections live on the persistent manager, so a device switch after the host
        // screen is freed would invoke a closure over a disposed node — sever them when the
        // tickbox leaves the tree.
        tickbox.Connect(Node.SignalName.TreeExiting, Callable.From(() =>
        {
            NControllerManager? m = NControllerManager.Instance;
            if (m != null)
            {
                m.Disconnect(NControllerManager.SignalName.MouseDetected, onDeviceChanged);
                m.Disconnect(NControllerManager.SignalName.ControllerDetected, onDeviceChanged);
            }
        }));
    }
}

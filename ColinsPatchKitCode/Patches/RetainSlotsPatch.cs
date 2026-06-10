using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

// When Well Laid Plans asks which cards to retain at end of turn, vanilla gives
// no indication of how many cards may be picked: selected cards just pack into
// a centered row. This patch shows one dotted card outline ("slot") per retain
// charge above the hand. Picking a card drops it into the leftmost empty slot
// and clicking a selected card sends it back to the hand, leaving its slot
// empty in place — so the number of remaining picks is always visible.
public static class RetainSlotsManager
{
    // The card frame spans 300x422 centered on the card origin (Frame node in
    // card.tscn), and selected-card holders render at NCardHolder.smallScale.
    private static readonly Vector2 _cardFrameSize = new(300f, 422f);
    private const float CornerRadius = 16f;
    private const float OutlineWidth = 3f;
    private const float DashLength = 12f;
    private static readonly Color _outlineColor = new(1f, 1f, 1f, 0.45f);
    private static readonly Color _fillColor = new(0f, 0f, 0f, 0.25f);

    // _selectedHandCardContainer is the private NPlayerHand node that holds the
    // row of picked cards during in-hand selection.
    private static readonly FieldInfo _containerField =
        typeof(NPlayerHand).GetField("_selectedHandCardContainer", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(nameof(NPlayerHand), "_selectedHandCardContainer");

    private static StyleBoxFlat? _fillStyle;

    private static Control? _overlay;
    private static NSelectedHandCardContainer? _container;
    private static int _slotCount;
    private static float _slotSpacing;
    private static Vector2? _measuredHolderSize;
    private static readonly Dictionary<NSelectedHandCardHolder, int> _slotByHolder = new();

    public static int SlotCount => _slotCount;

    public static bool Active =>
        _overlay != null && GodotObject.IsInstanceValid(_overlay)
        && _container != null && GodotObject.IsInstanceValid(_container);

    public static void Activate(NPlayerHand hand, int slotCount)
    {
        try
        {
            ActivateInternal(hand, slotCount);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to show retain slots: {e}");
            Deactivate();
        }
    }

    private static void ActivateInternal(NPlayerHand hand, int slotCount)
    {
        Deactivate();
        if (_containerField.GetValue(hand) is not NSelectedHandCardContainer container
            || !GodotObject.IsInstanceValid(container))
        {
            return;
        }
        _slotCount = slotCount;
        _slotSpacing = MeasureSlotSpacing();
        _container = container;
        Control overlay = new()
        {
            Name = "CpkRetainSlots",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        overlay.Draw += () => DrawSlots(overlay);
        _overlay = overlay;
        // Index 0 keeps the outlines behind the card holders the game appends.
        container.AddChildSafely(overlay);
        container.MoveChildSafely(overlay, 0);
    }

    public static void Deactivate()
    {
        _slotByHolder.Clear();
        _slotCount = 0;
        if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
        {
            _overlay.GetParent()?.RemoveChildSafely(_overlay);
            _overlay.QueueFreeSafelyNoPool();
        }
        _overlay = null;
        _container = null;
    }

    // Replaces NSelectedHandCardContainer.RefreshHolderPositions while a slot
    // selection is active: instead of re-packing the row around the center,
    // every holder keeps a stable slot, so deselecting leaves a visible gap.
    // Returns false when inactive or given some other container, in which case
    // the vanilla layout must run.
    public static bool TryLayout(NSelectedHandCardContainer container)
    {
        try
        {
            return TryLayoutInternal(container);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to lay out retain slots: {e}");
            return false;
        }
    }

    private static bool TryLayoutInternal(NSelectedHandCardContainer container)
    {
        if (!Active || container != _container)
        {
            return false;
        }
        List<NSelectedHandCardHolder> holders = container.Holders;
        foreach (NSelectedHandCardHolder stale in _slotByHolder.Keys.Where(h => !holders.Contains(h)).ToList())
        {
            _slotByHolder.Remove(stale);
        }
        // Children are ordered by selection time, so a newly picked card takes
        // the leftmost slot freed by any earlier deselection.
        foreach (NSelectedHandCardHolder holder in holders)
        {
            if (!_slotByHolder.ContainsKey(holder))
            {
                _slotByHolder[holder] = NextFreeSlot();
            }
        }
        foreach ((NSelectedHandCardHolder holder, int slot) in _slotByHolder)
        {
            holder.Position = SlotPosition(slot);
        }
        List<NSelectedHandCardHolder> ordered = holders.OrderBy(h => _slotByHolder[h]).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].FocusNeighborLeft = ordered[(i + ordered.Count - 1) % ordered.Count].GetPath();
            ordered[i].FocusNeighborRight = ordered[(i + 1) % ordered.Count].GetPath();
        }
        container.FocusMode = holders.Count > 0 ? Control.FocusModeEnum.All : Control.FocusModeEnum.None;
        _overlay!.QueueRedraw();
        return true;
    }

    private static int NextFreeSlot()
    {
        int slot = 0;
        while (_slotByHolder.ContainsValue(slot))
        {
            slot++;
        }
        return slot;
    }

    // Same centering math as the vanilla layout, but over all slots instead of
    // only the currently selected cards. Positions are card centers: holders
    // place their NCard at local (0,0).
    private static Vector2 SlotPosition(int slot)
    {
        return SlotPosition(slot, _slotCount, _slotSpacing);
    }

    internal static Vector2 SlotPosition(int slot, int slotCount, float spacing)
    {
        return new Vector2(spacing * (slot - (slotCount - 1) / 2f), 0f);
    }

    // Vanilla spacing is the selected-card holder's width, which only exists
    // once a card is picked; instantiate the holder scene once to know it
    // before the first pick.
    internal static float MeasureSlotSpacing()
    {
        if (_measuredHolderSize == null)
        {
            try
            {
                string scenePath = NSelectedHandCardHolder.AssetPaths.First();
                Control holder = PreloadManager.Cache.GetScene(scenePath).Instantiate<Control>();
                _measuredHolderSize = holder.Size;
                holder.Free();
            }
            catch (Exception e)
            {
                MainFile.Logger.Warn($"Could not measure selected card holder, using fallback spacing: {e}");
                _measuredHolderSize = Vector2.Zero;
            }
        }
        float width = _measuredHolderSize.Value.X;
        return width >= 50f ? width : _cardFrameSize.X * NCardHolder.smallScale.X + 16f;
    }

    private static void DrawSlots(Control overlay)
    {
        DrawSlots(overlay, _slotCount, _slotSpacing, _slotByHolder.Values.ToHashSet());
    }

    // Also the dev hook for the retain-slots preview tooling: draws the slot
    // row (minus the filled ones) on an arbitrary overlay without an active
    // selection.
    internal static void DrawSlots(Control overlay, int slotCount, float spacing, HashSet<int> filled)
    {
        Vector2 slotSize = _cardFrameSize * NCardHolder.smallScale.X;
        for (int i = 0; i < slotCount; i++)
        {
            if (!filled.Contains(i))
            {
                DrawSlotOutline(overlay, SlotPosition(i, slotCount, spacing), slotSize);
            }
        }
    }

    private static void DrawSlotOutline(Control overlay, Vector2 center, Vector2 size)
    {
        Rect2 rect = new(center - size * 0.5f, size);
        float r = CornerRadius;
        _fillStyle ??= CreateFillStyle();
        _fillStyle.Draw(overlay.GetCanvasItem(), rect);
        Vector2 topLeft = rect.Position;
        Vector2 bottomRight = rect.End;
        overlay.DrawDashedLine(new Vector2(topLeft.X + r, topLeft.Y), new Vector2(bottomRight.X - r, topLeft.Y),
            _outlineColor, OutlineWidth, DashLength);
        overlay.DrawDashedLine(new Vector2(bottomRight.X, topLeft.Y + r), new Vector2(bottomRight.X, bottomRight.Y - r),
            _outlineColor, OutlineWidth, DashLength);
        overlay.DrawDashedLine(new Vector2(bottomRight.X - r, bottomRight.Y), new Vector2(topLeft.X + r, bottomRight.Y),
            _outlineColor, OutlineWidth, DashLength);
        overlay.DrawDashedLine(new Vector2(topLeft.X, bottomRight.Y - r), new Vector2(topLeft.X, topLeft.Y + r),
            _outlineColor, OutlineWidth, DashLength);
        overlay.DrawArc(new Vector2(topLeft.X + r, topLeft.Y + r), r, Mathf.Pi, 1.5f * Mathf.Pi, 8,
            _outlineColor, OutlineWidth, antialiased: true);
        overlay.DrawArc(new Vector2(bottomRight.X - r, topLeft.Y + r), r, 1.5f * Mathf.Pi, 2f * Mathf.Pi, 8,
            _outlineColor, OutlineWidth, antialiased: true);
        overlay.DrawArc(new Vector2(bottomRight.X - r, bottomRight.Y - r), r, 0f, 0.5f * Mathf.Pi, 8,
            _outlineColor, OutlineWidth, antialiased: true);
        overlay.DrawArc(new Vector2(topLeft.X + r, bottomRight.Y - r), r, 0.5f * Mathf.Pi, Mathf.Pi, 8,
            _outlineColor, OutlineWidth, antialiased: true);
    }

    private static StyleBoxFlat CreateFillStyle()
    {
        StyleBoxFlat style = new() { BgColor = _fillColor };
        style.SetCornerRadiusAll((int)CornerRadius);
        return style;
    }
}

// SelectCards' synchronous prelude (mode, prefs, header, card filtering) runs
// before the postfix, so the eligible-card count can be read from the holders
// vanilla just finished filtering. Gated to Well Laid Plans — the only effect
// that lets the player choose cards to retain.
[HarmonyPatch(typeof(NPlayerHand), nameof(NPlayerHand.SelectCards))]
public static class RetainSlotsSelectCardsPatch
{
    public static void Postfix(NPlayerHand __instance, CardSelectorPrefs prefs, AbstractModel? source,
        NPlayerHand.Mode mode)
    {
        if (!ColinsPatchKitConfig.ShowWellLaidPlansRetainSlots
            || mode != NPlayerHand.Mode.SimpleSelect
            || source is not WellLaidPlansPower)
        {
            return;
        }
        // Never show more slots than there are cards eligible to retain.
        int slotCount = Math.Min(prefs.MaxSelect, __instance.ActiveHolders.Count);
        if (slotCount > 0)
        {
            RetainSlotsManager.Activate(__instance, slotCount);
        }
    }
}

// AfterCardsSelected runs on confirm, cancel, and combat teardown — every exit
// path out of selection mode.
[HarmonyPatch(typeof(NPlayerHand), "AfterCardsSelected")]
public static class RetainSlotsSelectionEndedPatch
{
    public static void Postfix()
    {
        RetainSlotsManager.Deactivate();
    }
}

[HarmonyPatch(typeof(NSelectedHandCardContainer), "RefreshHolderPositions")]
public static class RetainSlotsLayoutPatch
{
    public static bool Prefix(NSelectedHandCardContainer __instance)
    {
        return !RetainSlotsManager.TryLayout(__instance);
    }
}

// Vanilla repositions/rescales the selected-card row from the live child count
// as cards come and go (and would also count the overlay node). Sizing for the
// full slot row up front keeps the slots and cards from shifting mid-pick.
[HarmonyPatch(typeof(NPlayerHand), "UpdateSelectedCardContainer")]
public static class RetainSlotsContainerSizePatch
{
    public static void Prefix(ref int count)
    {
        if (RetainSlotsManager.Active)
        {
            count = RetainSlotsManager.SlotCount;
        }
    }
}

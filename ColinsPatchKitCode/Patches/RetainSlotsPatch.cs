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
    private const float OutlineWidth = 2.25f;
    // Pixels trimmed from each edge of the full card-frame footprint, so a
    // settled card (rendered at full size) covers the outline instead of
    // leaving dashes peeking around the edges. Tunable; 0 = exact card-frame
    // size. Measured in UI-space pixels (same units the outline is drawn in).
    private const float SlotInset = 4f;
    private const float DashLength = 12f;
    // Opaque, NOT translucent: a dash that wraps a corner is one antialiased
    // polyline, and Godot overdraws its own anti-aliasing at the internal corner
    // joints — with a translucent color those two layers add up to bright-white
    // specks at the corners. An opaque gray (matched to how the old 45%-white
    // read over the dark slot) can't stack, so the corners stay clean.
    private static readonly Color _outlineColor = new(0.6f, 0.6f, 0.62f, 1f);
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
        // Draw every slot's outline, including ones already assigned a card. The
        // overlay sits behind the card holders, so a settled card covers its
        // (slightly smaller) slot, while a card still animating in from the hand
        // leaves the outline visible underneath until it arrives — without this
        // the outline vanished the instant a card was picked, before it had slid
        // into the slot.
        DrawSlots(overlay, _slotCount, _slotSpacing, new HashSet<int>());
    }

    // Also the dev hook for the retain-slots preview tooling: draws the slot
    // row (minus the filled ones) on an arbitrary overlay without an active
    // selection.
    internal static void DrawSlots(Control overlay, int slotCount, float spacing, HashSet<int> filled)
    {
        Vector2 slotSize = _cardFrameSize * NCardHolder.smallScale.X - new Vector2(SlotInset * 2f, SlotInset * 2f);
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
        _fillStyle ??= CreateFillStyle();
        _fillStyle.Draw(overlay.GetCanvasItem(), rect);
        // One continuous dashed loop traced clockwise around the rounded-rect
        // perimeter, so the dash pattern flows evenly through the corners
        // instead of restarting (and doubling up) at each side/arc seam.
        DrawDashedRoundedRect(overlay, rect, CornerRadius, _outlineColor, OutlineWidth, DashLength);
    }

    private static void DrawDashedRoundedRect(Control overlay, Rect2 rect, float radius, Color color,
        float width, float dashLength)
    {
        Vector2 tl = rect.Position;
        Vector2 br = rect.End;
        float r = Mathf.Min(radius, Mathf.Min(rect.Size.X, rect.Size.Y) * 0.5f);
        const int cornerSegments = 6;
        List<Vector2> path = new();
        // Top edge, then each corner arc (Godot angles: 0 = +X, +PI/2 = +Y/down).
        path.Add(new Vector2(tl.X + r, tl.Y));
        path.Add(new Vector2(br.X - r, tl.Y));
        AppendArc(path, new Vector2(br.X - r, tl.Y + r), r, -Mathf.Pi / 2f, 0f, cornerSegments);
        path.Add(new Vector2(br.X, br.Y - r));
        AppendArc(path, new Vector2(br.X - r, br.Y - r), r, 0f, Mathf.Pi / 2f, cornerSegments);
        path.Add(new Vector2(tl.X + r, br.Y));
        AppendArc(path, new Vector2(tl.X + r, br.Y - r), r, Mathf.Pi / 2f, Mathf.Pi, cornerSegments);
        path.Add(new Vector2(tl.X, tl.Y + r));
        AppendArc(path, new Vector2(tl.X + r, tl.Y + r), r, Mathf.Pi, Mathf.Pi * 1.5f, cornerSegments);

        // Snap the dash period to a whole number of equal dash+gap cycles around
        // the perimeter so the pattern closes seamlessly at the loop's start and
        // the corners always land on the same phase — otherwise a leftover
        // partial dash makes the seam and corners look uneven. dashLength is the
        // target; the actual dash is nudged to the nearest evenly-dividing size.
        float perimeter = ClosedPathLength(path);
        int cycles = Mathf.Max(1, Mathf.RoundToInt(perimeter / (dashLength * 2f)));
        float evenDash = perimeter / cycles / 2f;
        DrawDashedLoop(overlay, path, color, width, evenDash);
    }

    private static float ClosedPathLength(List<Vector2> path)
    {
        float length = 0f;
        for (int i = 0; i < path.Count; i++)
        {
            length += path[i].DistanceTo(path[(i + 1) % path.Count]);
        }
        return length;
    }

    private static void AppendArc(List<Vector2> path, Vector2 center, float r, float startAngle,
        float endAngle, int segments)
    {
        for (int i = 0; i <= segments; i++)
        {
            float t = Mathf.Lerp(startAngle, endAngle, (float)i / segments);
            path.Add(center + new Vector2(Mathf.Cos(t), Mathf.Sin(t)) * r);
        }
    }

    // Walks a closed polyline at a constant arc-length dash period (dash on for
    // dashLength, off for dashLength), splitting each segment so dashes stay
    // uniform across vertices — the bit DrawDashedLine can't do around corners.
    // Each "on" dash is accumulated and drawn as a single antialiased polyline
    // (including any corner vertices it spans); drawing the corner facets as
    // separate DrawLine calls would overlap their antialiased caps at each
    // joint and stack the translucent color into bright specks.
    private static void DrawDashedLoop(Control overlay, List<Vector2> path, Color color, float width,
        float dashLength)
    {
        float period = dashLength * 2f;
        float phase = 0f;
        List<Vector2> dash = new();
        void FlushDash()
        {
            if (dash.Count >= 2)
            {
                overlay.DrawPolyline(dash.ToArray(), color, width, antialiased: true);
            }
            dash.Clear();
        }
        for (int i = 0; i < path.Count; i++)
        {
            Vector2 a = path[i];
            Vector2 b = path[(i + 1) % path.Count];
            float segLen = a.DistanceTo(b);
            if (segLen <= 0.001f)
            {
                continue;
            }
            Vector2 dir = (b - a) / segLen;
            float pos = 0f;
            while (pos < segLen)
            {
                bool on = phase < dashLength;
                float remainingInPhase = (on ? dashLength : period) - phase;
                float step = Mathf.Min(remainingInPhase, segLen - pos);
                if (on)
                {
                    if (dash.Count == 0)
                    {
                        dash.Add(a + dir * pos);
                    }
                    dash.Add(a + dir * (pos + step));
                }
                else
                {
                    FlushDash();
                }
                pos += step;
                phase = (phase + step) % period;
            }
        }
        FlushDash();
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

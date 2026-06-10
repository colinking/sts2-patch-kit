using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace ColinsPatchKit.ColinsPatchKitCode.Patches;

// Renders the colored frame of Ethereal cards slightly translucent while the
// text panel, art, banner, energy gem and text stay fully opaque. The frame
// texture bakes the colored border and the dark text panel into one sprite,
// so a plain alpha on the frame node would fade the panel behind the card
// text too; instead we swap in a copy of vanilla's frame material (the HSV
// tint in shaders/hsv.gdshader) extended to fade only saturated pixels —
// the border ring is strongly saturated in the source texture (~0.7) while
// the text panel is near-gray (~0.2).
public static class EtherealTransparencyManager
{
    private const float FrameAlpha = 0.8f;

    // Vanilla's hsv.gdshader pipeline (YIQ hue/sat/value shift, then
    // modulate), plus the saturation-keyed alpha fade on the final line.
    private const string FrameShaderCode = """
        shader_type canvas_item;

        uniform float h: hint_range(0.0, 1.0) = 1.0;
        uniform float s: hint_range(0.0, 5.0) = 1.0;
        uniform float v = 1.0;
        uniform float frame_alpha = 1.0;

        varying vec4 modulate_color;

        void vertex() {
            modulate_color = COLOR;
        }

        void fragment() {
            mat3 RGB_to_YIQ = mat3(
                vec3(0.2989,  0.5959,  0.2115),
                vec3(0.5870, -0.2774, -0.5229),
                vec3(0.1140, -0.3216,  0.3114));

            vec4 col = texture(TEXTURE, UV);

            float cmax = max(col.r, max(col.g, col.b));
            float cmin = min(col.r, min(col.g, col.b));
            float src_sat = (cmax - cmin) / max(cmax, 0.0001);

            col.rgb = RGB_to_YIQ * col.rgb;
            float hue = mix(0.0, 6.283185, 1.0 - h);
            float sin_hue = sin(hue);
            float cos_hue = cos(hue);
            mat3 hue_shift = mat3(
                vec3(1.0, 0.0, 0.0),
                vec3(0.0, cos_hue, -sin_hue),
                vec3(0.0, sin_hue, cos_hue));
            col.rgb *= hue_shift;
            mat3 sat_shift = mat3(
                vec3(1.0, 0.0, 0.0),
                vec3(0.0, s, 0.0),
                vec3(0.0, 0.0, s));
            col.rgb = sat_shift * col.rgb;
            col.rgb = mix(vec3(0.0), col.rgb, v);
            col.rgb = inverse(RGB_to_YIQ) * col.rgb;
            COLOR = col * modulate_color;
            COLOR.a *= mix(1.0, frame_alpha, smoothstep(0.30, 0.55, src_sat));
        }
        """;

    private static Shader? _frameShader;

    private static Shader FrameShader => _frameShader ??= new Shader { Code = FrameShaderCode };

    // Marks materials we created, so the restore path never touches a
    // material some other system set on the frame.
    private const string FadedMeta = "cpk_ethereal_fade";

    // One faded material per vanilla frame material (they are shared per card
    // color via the preload cache, so this stays tiny).
    private static readonly ConditionalWeakTable<Material, ShaderMaterial> _fadedMaterials = new();

    private static readonly ConditionalWeakTable<NCard, Action> _keywordHandlers = new();

    // _frame is private on NCard; it holds the frame TextureRect whose
    // material vanilla reassigns right before calling ReloadOverlay.
    private static readonly FieldInfo _frameField =
        typeof(NCard).GetField("_frame", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(nameof(NCard), "_frame");

    public static void Refresh(NCard card)
    {
        try
        {
            RefreshInternal(card);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to refresh ethereal transparency: {e}");
        }
    }

    private static void RefreshInternal(NCard card)
    {
        TextureRect? frame = (TextureRect?)_frameField.GetValue(card);
        if (frame == null || !GodotObject.IsInstanceValid(frame))
        {
            return;
        }
        CardModel? model = card.Model;
        // Quest frames invert the texture layout (gray border, tinted panel)
        // and Ancient cards use a separate border node, so the saturation
        // mask would fade the wrong pixels; both keep their vanilla frame.
        bool shouldFade = ColinsPatchKitConfig.MakeEtherealCardsTranslucent
            && model?.Keywords.Contains(CardKeyword.Ethereal) == true
            && model.Type != CardType.Quest
            && model.Rarity != CardRarity.Ancient;
        if (shouldFade)
        {
            if (model!.FrameMaterial is ShaderMaterial vanillaMaterial)
            {
                frame.Material = GetFadedMaterial(vanillaMaterial);
            }
        }
        else if (frame.Material is ShaderMaterial current && current.HasMeta(FadedMeta))
        {
            frame.Material = model?.FrameMaterial;
        }
    }

    private static ShaderMaterial GetFadedMaterial(ShaderMaterial vanillaMaterial)
    {
        if (_fadedMaterials.TryGetValue(vanillaMaterial, out ShaderMaterial? faded))
        {
            return faded;
        }
        faded = CreateFadedMaterial(vanillaMaterial);
        _fadedMaterials.Add(vanillaMaterial, faded);
        return faded;
    }

    private static ShaderMaterial CreateFadedMaterial(ShaderMaterial vanillaMaterial)
    {
        ShaderMaterial faded = new() { Shader = FrameShader };
        foreach (string parameter in (string[])["h", "s", "v"])
        {
            Variant value = vanillaMaterial.GetShaderParameter(parameter);
            if (value.VariantType != Variant.Type.Nil)
            {
                faded.SetShaderParameter(parameter, value);
            }
        }
        faded.SetShaderParameter("frame_alpha", FrameAlpha);
        faded.SetMeta(FadedMeta, true);
        return faded;
    }

    // Walks the scene tree, re-evaluating the frame fade for every live card.
    // Used when the config toggle changes.
    public static void RefreshAllCards()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
        {
            return;
        }
        Stack<Node> pending = new();
        pending.Push(tree.Root);
        while (pending.Count > 0)
        {
            Node node = pending.Pop();
            if (node is NCard card)
            {
                if (card.Model is CardModel model && card.IsInsideTree())
                {
                    AttachKeywordHandler(card, model);
                }
                Refresh(card);
            }
            foreach (Node child in node.GetChildren())
            {
                pending.Push(child);
            }
        }
    }

    public static void AttachKeywordHandler(NCard card, CardModel model)
    {
        if (_keywordHandlers.TryGetValue(card, out _))
        {
            return;
        }
        Action handler = () => Refresh(card);
        model.KeywordsChanged += handler;
        _keywordHandlers.Add(card, handler);
    }

    public static void DetachKeywordHandler(NCard card, CardModel model)
    {
        if (_keywordHandlers.TryGetValue(card, out Action? handler))
        {
            model.KeywordsChanged -= handler;
            _keywordHandlers.Remove(card);
        }
    }
}

// ReloadOverlay runs whenever the card re-syncs its look from the model —
// notably right after vanilla assigns _frame.Material = Model.FrameMaterial,
// so swapping the material here can never be stomped by that assignment.
[HarmonyPatch(typeof(NCard), "ReloadOverlay")]
public static class EtherealTransparencyReloadPatch
{
    public static void Postfix(NCard __instance)
    {
        EtherealTransparencyManager.Refresh(__instance);
    }
}

// Vanilla NCard only listens for affliction/enchantment changes; keywords can
// change at runtime too (CardModel.AddKeyword/RemoveKeyword), so cards gaining
// or losing Ethereal mid-combat need their own subscription.
[HarmonyPatch(typeof(NCard), "SubscribeToModel")]
public static class EtherealTransparencySubscribePatch
{
    public static void Postfix(NCard __instance, CardModel? model)
    {
        if (model != null && __instance.IsInsideTree())
        {
            EtherealTransparencyManager.AttachKeywordHandler(__instance, model);
        }
    }
}

[HarmonyPatch(typeof(NCard), "UnsubscribeFromModel")]
public static class EtherealTransparencyUnsubscribePatch
{
    public static void Postfix(NCard __instance, CardModel? model)
    {
        if (model != null)
        {
            EtherealTransparencyManager.DetachKeywordHandler(__instance, model);
        }
    }
}

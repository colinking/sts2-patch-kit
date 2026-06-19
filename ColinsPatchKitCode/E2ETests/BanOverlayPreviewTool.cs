using System;
using System.Collections.Generic;
using System.Linq;
using ColinsPatchKit.ColinsPatchKitCode.Patches;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace ColinsPatchKit.ColinsPatchKitCode.E2ETests;

// Dev harness: static visual comparison of the banned-character overlay options.
// Renders a grid at the main menu — the top row is the five characters with no
// overlay, then one row per overlay PNG found in ColinsPatchKit/assets/, each
// stretched over the same five portraits exactly the way the real exclusion mark
// is (StretchMode.Scale over the icon rect). Saves a single screenshot so every
// candidate overlay can be eyeballed side by side without starting a run:
//
//   "Slay the Spire 2" --banoverlay-shot=/tmp/ban_overlays.png
//
// Pass --banoverlay-portraits=<dir> alongside it to also dump each character's
// select icon to PNGs, which the standalone MegaDot preview (tools/) composites
// without needing the game.
//
// The grid is drawn into a SubViewport sized to the whole composite, so it is not
// clipped by the 1280x800 test window. The harness screenshots and quits.
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class BanOverlayPreviewPatch
{
    private const string AssetsDir = "res://ColinsPatchKit/assets";

    // Portrait cell + layout metrics (logical px in the composite).
    private const float PortraitHeight = 240f;
    private const float CellGapX = 28f;
    private const float RowGapY = 56f;
    private const float LabelColWidth = 320f;
    private const float HeaderHeight = 48f;
    private const float Margin = 40f;

    private static bool _ran;

    public static void Postfix(NMainMenu __instance)
    {
        if (_ran || !CommandLineHelper.TryGetValue("banoverlay-shot", out string? shotPath)
            || string.IsNullOrEmpty(shotPath))
        {
            return;
        }
        _ran = true;
        SceneTree tree = __instance.GetTree();
        tree.CreateTimer(1.0).Timeout += () => Run(shotPath, tree);
    }

    private static void Run(string shotPath, SceneTree tree)
    {
        try
        {
            List<CharacterModel> characters = ModelDb.AllCharacters.ToList();
            List<(string name, Texture2D tex)> overlays = LoadOverlays();
            if (characters.Count == 0)
            {
                MainFile.Logger.Error("banoverlay: no characters found.");
                tree.Quit();
                return;
            }
            MainFile.Logger.Info($"banoverlay: {characters.Count} characters, "
                + $"{overlays.Count} overlay(s): {string.Join(", ", overlays.Select(o => o.name))}.");

            // One-time extraction: dump each character's select icon to PNGs so the standalone
            // MegaDot preview (tools/) can composite without the game. Game-only; the cache is reused.
            if (CommandLineHelper.TryGetValue("banoverlay-portraits", out string? dumpDir) && !string.IsNullOrEmpty(dumpDir))
            {
                DirAccess.MakeDirRecursiveAbsolute(dumpDir);
                foreach (CharacterModel character in characters)
                {
                    string outPath = $"{dumpDir}/{character.Id.Entry}.png";
                    Error dumpErr = character.CharacterSelectIcon.GetImage().SavePng(outPath);
                    MainFile.Logger.Info($"banoverlay: dumped portrait '{outPath}' ({dumpErr}).");
                }
            }

            // Size the cell to the first portrait's aspect so portraits aren't distorted; the
            // overlay then stretches to that same rect, mirroring the live mark.Size = icon.Size.
            Texture2D firstIcon = characters[0].CharacterSelectIcon;
            float aspect = firstIcon.GetWidth() / (float)firstIcon.GetHeight();
            float cellH = PortraitHeight;
            float cellW = cellH * aspect;

            int rows = 1 + overlays.Count; // normal row + one per overlay
            float gridLeft = Margin + LabelColWidth;
            float gridTop = Margin + HeaderHeight;
            float width = gridLeft + characters.Count * cellW + (characters.Count - 1) * CellGapX + Margin;
            float height = gridTop + rows * cellH + (rows - 1) * RowGapY + Margin;

            var viewportSize = new Vector2I(Mathf.CeilToInt(width), Mathf.CeilToInt(height));
            var subViewport = new SubViewport
            {
                Size = viewportSize,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                RenderTargetClearMode = SubViewport.ClearMode.Always,
                TransparentBg = false,
            };
            NGame.Instance!.AddChildSafely(subViewport);

            // White backdrop so any overlay bleeding past a portrait's edge is obvious.
            var backdrop = new ColorRect { Color = Colors.White, Size = viewportSize };
            subViewport.AddChild(backdrop);

            // Column headers: character id above each column.
            for (int c = 0; c < characters.Count; c++)
            {
                float x = gridLeft + c * (cellW + CellGapX);
                AddLabel(subViewport, characters[c].Id.Entry,
                    new Vector2(x, Margin), new Vector2(cellW, HeaderHeight),
                    HorizontalAlignment.Center, VerticalAlignment.Bottom);
            }

            // Row 0 is "Normal" (no overlay); rows 1..N each apply one overlay.
            for (int r = 0; r < rows; r++)
            {
                float y = gridTop + r * (cellH + RowGapY);
                string rowLabel = r == 0 ? "Normal" : overlays[r - 1].name;
                Texture2D? overlayTex = r == 0 ? null : overlays[r - 1].tex;

                AddLabel(subViewport, rowLabel,
                    new Vector2(Margin, y), new Vector2(LabelColWidth - 16f, cellH),
                    HorizontalAlignment.Right, VerticalAlignment.Center, 22);

                for (int c = 0; c < characters.Count; c++)
                {
                    float x = gridLeft + c * (cellW + CellGapX);
                    var cellPos = new Vector2(x, y);
                    var cellSize = new Vector2(cellW, cellH);

                    subViewport.AddChild(MakeCell(characters[c].CharacterSelectIcon, cellPos, cellSize));

                    if (overlayTex != null)
                    {
                        // Mirror the live exclusion mark exactly: stretched over the icon rect.
                        subViewport.AddChild(MakeCell(overlayTex, cellPos, cellSize));
                    }
                }
            }

            // Give the SubViewport a couple of frames to render, then capture and quit.
            tree.CreateTimer(0.75).Timeout += () =>
            {
                Image image = subViewport.GetTexture().GetImage();
                Error err = image.SavePng(shotPath);
                MainFile.Logger.Info($"banoverlay: screenshot to '{shotPath}' ({err}), "
                    + $"composite {viewportSize.X}x{viewportSize.Y}.");
                tree.Quit();
            };
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"banoverlay: preview failed: {e}");
            tree.Quit();
        }
    }

    // A TextureRect stretched to exactly `size`, matching the live exclusion mark. ExpandMode is
    // set to IgnoreSize *before* Texture/Size: assigning Texture while the default KeepSize is still
    // active would raise the control's minimum size to the texture's native size, and the later
    // `Size = size` would be clamped up to that — drawing the texture far larger than the cell.
    private static TextureRect MakeCell(Texture2D texture, Vector2 pos, Vector2 size)
    {
        return new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            Texture = texture,
            Position = pos,
            Size = size,
        };
    }

    // Every .png under the assets dir is an overlay candidate, sorted by name for a stable order.
    private static List<(string, Texture2D)> LoadOverlays()
    {
        var result = new List<(string, Texture2D)>();
        // In an exported pck, imported textures appear as ".png.import" (and sometimes ".remap"),
        // not as the source ".png" — strip those suffixes back to the loadable source name.
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in DirAccess.GetFilesAt(AssetsDir))
        {
            string name = file;
            if (name.EndsWith(".import", StringComparison.Ordinal))
            {
                name = name[..^".import".Length];
            }
            else if (name.EndsWith(".remap", StringComparison.Ordinal))
            {
                name = name[..^".remap".Length];
            }
            if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }
        foreach (string name in names)
        {
            string path = $"{AssetsDir}/{name}";
            var tex = ResourceLoader.Load<Texture2D>(path);
            if (tex != null)
            {
                result.Add((name, tex));
            }
            else
            {
                MainFile.Logger.Error($"banoverlay: failed to load overlay '{path}'.");
            }
        }
        return result;
    }

    private static void AddLabel(Node parent, string text, Vector2 pos, Vector2 size,
        HorizontalAlignment hAlign, VerticalAlignment vAlign, int fontSize = 26)
    {
        var label = new Label
        {
            Text = text,
            Position = pos,
            Size = size,
            HorizontalAlignment = hAlign,
            VerticalAlignment = vAlign,
            AutowrapMode = TextServer.AutowrapMode.Off,
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", Colors.Black); // readable on the white backdrop
        parent.AddChild(label);
    }
}

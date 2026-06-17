using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace ColinsPatchKit.Tests;

// Static guards over the mod's localization so a stray key, a missing translation, or a blank
// value is caught at `dotnet test` time rather than as a missing-string (or SmartFormat crash) in
// game. Everything here reads the repo's source files directly — no game, no built assembly — so
// it runs on any machine. See CLAUDE.md "Localization" for how the game merges these tables.
public class LocalizationTests
{
    // The locales the base game ships (eng plus 13), read from the game .pck path table. The mod
    // must provide a translation for exactly these — no more (a typo'd dir), no fewer (an untranslated
    // language has no eng fallback for our keys, so its strings would break). Update this list only
    // when the game itself adds/removes a supported locale.
    private static readonly string[] SupportedLocales =
        { "eng", "deu", "esp", "spa", "fra", "ita", "jpn", "kor", "pol", "ptb", "rus", "tha", "tur", "zhs" };

    // The loc-table files the mod contributes (each merged into the same-named base-game table).
    private static readonly string[] LocFileNames = { "settings_ui.json", "map.json" };

    // Keys in the `map` table are referenced from code as Loc("X") and stored as
    // "COLINSPATCHKIT-MAPINFO-X"; keep this in sync with CpkLocPrefix in MapNodeInfoTooltipPatch.cs.
    private const string MapKeyPrefix = "COLINSPATCHKIT-MAPINFO-";

    // ---- (1) Every supported locale is present, with both loc files, and nothing extra. ----

    [Fact]
    public void AllSupportedLocalesArePresent()
    {
        string[] actual = Directory.GetDirectories(LocalizationDir)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .OrderBy(n => n)
            .ToArray();

        string[] missing = SupportedLocales.Except(actual).OrderBy(s => s).ToArray();
        string[] extra = actual.Except(SupportedLocales).OrderBy(s => s).ToArray();

        Assert.True(missing.Length == 0 && extra.Length == 0,
            $"Locale directories must be exactly the supported set.\n" +
            $"  Missing: {Fmt(missing)}\n  Unexpected: {Fmt(extra)}");

        List<string> noFiles = new();
        foreach (string locale in SupportedLocales)
        {
            foreach (string file in LocFileNames)
            {
                if (!File.Exists(Path.Combine(LocalizationDir, locale, file)))
                {
                    noFiles.Add($"{locale}/{file}");
                }
            }
        }

        Assert.True(noFiles.Count == 0, $"Missing loc files: {Fmt(noFiles)}");
    }

    // ---- (2) Every locale's file carries the exact same key set as the English source. ----

    [Fact]
    public void EveryLocaleSharesEnglishKeySet()
    {
        List<string> failures = new();
        foreach (string file in LocFileNames)
        {
            HashSet<string> englishKeys = LoadTable("eng", file).Keys.ToHashSet();
            foreach (string locale in SupportedLocales)
            {
                if (locale == "eng")
                {
                    continue;
                }
                HashSet<string> keys = LoadTable(locale, file).Keys.ToHashSet();
                string[] missing = englishKeys.Except(keys).OrderBy(k => k).ToArray();
                string[] extra = keys.Except(englishKeys).OrderBy(k => k).ToArray();
                if (missing.Length > 0 || extra.Length > 0)
                {
                    failures.Add($"{locale}/{file}: missing {Fmt(missing)}, extra {Fmt(extra)}");
                }
            }
        }

        Assert.True(failures.Count == 0, "Key sets diverge from English:\n  " + string.Join("\n  ", failures));
    }

    // ---- (3) No loc value is empty or whitespace-only, in any file. ----

    [Fact]
    public void NoLocValueIsEmpty()
    {
        List<string> blanks = new();
        foreach (string locale in SupportedLocales)
        {
            foreach (string file in LocFileNames)
            {
                foreach ((string key, string value) in LoadTable(locale, file))
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        blanks.Add($"{locale}/{file} [{key}]");
                    }
                }
            }
        }

        Assert.True(blanks.Count == 0, "Empty loc values:\n  " + string.Join("\n  ", blanks));
    }

    // ---- (4a) Every map-table key referenced via Loc("X") in code exists in the loc files. ----

    [Fact]
    public void CodeMapLocReferencesAllExist()
    {
        HashSet<string> englishMapKeys = LoadTable("eng", "map.json").Keys.ToHashSet();
        string[] dangling = ReferencedMapKeys()
            .Where(k => !englishMapKeys.Contains(MapKeyPrefix + k))
            .OrderBy(k => k)
            .ToArray();

        Assert.True(dangling.Length == 0,
            $"Code references map loc keys not in map.json: {Fmt(dangling.Select(k => MapKeyPrefix + k))}");
    }

    // ---- (4b, bonus) Every map-table key in the loc files is actually used by code. ----
    // Keeps the files honest — a key no code path can reach is dead weight (and untranslated work).

    [Fact]
    public void NoUnusedMapLocKeys()
    {
        HashSet<string> referenced = ReferencedMapKeys().Select(k => MapKeyPrefix + k).ToHashSet();
        string[] unused = LoadTable("eng", "map.json").Keys
            .Where(k => !referenced.Contains(k))
            .OrderBy(k => k)
            .ToArray();

        Assert.True(unused.Length == 0, $"map.json keys never referenced in code: {Fmt(unused)}");
    }

    // ---- (4c) The config toggles' derived settings_ui keys exactly match the loc file. ----
    // Each ColinsPatchKitConfig bool property and [ConfigSection] implies specific
    // COLINSPATCHKIT-<SCREAMING_SNAKE>.{title[,hover.title,hover.desc]} keys; assert the loc file
    // has precisely those — catching both a new toggle missing localization and a stale leftover key.

    [Fact]
    public void ConfigSettingsKeysExactlyMatchLocFile()
    {
        HashSet<string> expected = ExpectedSettingsKeys();
        HashSet<string> actual = LoadTable("eng", "settings_ui.json").Keys.ToHashSet();

        string[] missing = expected.Except(actual).OrderBy(k => k).ToArray();
        string[] stale = actual.Except(expected).OrderBy(k => k).ToArray();

        Assert.True(missing.Length == 0 && stale.Length == 0,
            $"settings_ui.json out of sync with ColinsPatchKitConfig.\n" +
            $"  Missing (config has no loc key): {Fmt(missing)}\n" +
            $"  Stale (loc key has no config): {Fmt(stale)}");
    }

    // --------------------------- helpers ---------------------------

    private static readonly Lazy<string> RepoRootLazy = new(() =>
    {
        // Walk up from the test assembly until we find the mod's main project file.
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ColinsPatchKit.csproj")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new DirectoryNotFoundException(
            "Could not locate repo root (ColinsPatchKit.csproj) above " + AppContext.BaseDirectory);
    });

    private static string RepoRoot => RepoRootLazy.Value;
    private static string LocalizationDir => Path.Combine(RepoRoot, "ColinsPatchKit", "localization");
    private static string CodeDir => Path.Combine(RepoRoot, "ColinsPatchKitCode");
    private static string ConfigFile => Path.Combine(CodeDir, "ColinsPatchKitConfig.cs");

    private static Dictionary<string, string> LoadTable(string locale, string file)
    {
        string path = Path.Combine(LocalizationDir, locale, file);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
            ?? throw new JsonException($"Failed to parse {locale}/{file}");
    }

    // Matches an ALL-CAPS-SNAKE string literal (our key shape: ROOM_EVENT, POTION, LABEL_VALUE, ...).
    // Mixed-case literals like "Event"/"Monster" (logic tokens) and SmartFormat var names don't match.
    private static readonly Regex KeyLiteral = new("\"([A-Z][A-Z0-9_]+)\"", RegexOptions.Compiled);

    // Map keys the code can reach: bare keys from any `Loc(...)` call site. We scan lines containing
    // "Loc(" and pull the all-caps literals on them — this catches the direct Loc("X") form and the
    // ternary Loc(cond ? "A" : "B") form alike, without matching unrelated all-caps constants
    // (e.g. hex colors) that live on non-Loc lines.
    private static HashSet<string> ReferencedMapKeys()
    {
        HashSet<string> keys = new();
        foreach (string csFile in Directory.EnumerateFiles(CodeDir, "*.cs", SearchOption.AllDirectories))
        {
            foreach (string line in File.ReadLines(csFile))
            {
                if (!line.Contains("Loc("))
                {
                    continue;
                }
                foreach (Match m in KeyLiteral.Matches(line))
                {
                    keys.Add(m.Groups[1].Value);
                }
            }
        }
        return keys;
    }

    // The full settings_ui key set implied by ColinsPatchKitConfig: one ".title" per [ConfigSection],
    // and ".title"/".hover.title"/".hover.desc" per bool toggle that carries [ConfigHoverTip(true)].
    private static HashSet<string> ExpectedSettingsKeys()
    {
        HashSet<string> expected = new();
        bool pendingHover = false;
        Regex section = new(@"\[ConfigSection\(""([^""]+)""\)\]");
        Regex hover = new(@"ConfigHoverTip\(true\)");
        Regex toggle = new(@"public static bool (\w+)");

        foreach (string line in File.ReadLines(ConfigFile))
        {
            Match s = section.Match(line);
            if (s.Success)
            {
                expected.Add($"COLINSPATCHKIT-{ScreamingSnake(s.Groups[1].Value)}.title");
            }
            if (hover.IsMatch(line))
            {
                pendingHover = true;
            }
            Match t = toggle.Match(line);
            if (t.Success)
            {
                string baseKey = $"COLINSPATCHKIT-{ScreamingSnake(t.Groups[1].Value)}";
                expected.Add($"{baseKey}.title");
                if (pendingHover)
                {
                    expected.Add($"{baseKey}.hover.title");
                    expected.Add($"{baseKey}.hover.desc");
                }
                pendingHover = false;
            }
        }
        return expected;
    }

    // PascalCase -> SCREAMING_SNAKE (ShowPotionChances -> SHOW_POTION_CHANCES, SpeedUps -> SPEED_UPS).
    private static string ScreamingSnake(string name)
    {
        StringBuilder sb = new();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
            {
                sb.Append('_');
            }
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    private static string Fmt(IEnumerable<string> items)
    {
        string joined = string.Join(", ", items);
        return joined.Length == 0 ? "(none)" : joined;
    }
}

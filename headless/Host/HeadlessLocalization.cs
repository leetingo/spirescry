// Populate the engine's LocManager from JSON tables that headless-setup
// extracted out of the game's .pck (Godot's res:// resolver doesn't exist
// here). Language comes from STS2_AGENT_LANG (default eng); non-eng tables
// fall back to eng per key.

namespace Spirescry.Host;

internal static class HeadlessLocalization
{
    public static void Init()
    {
        try
        {
            var libDir = Environment.GetEnvironmentVariable("STS2_HEADLESS_LIB");
            if (string.IsNullOrEmpty(libDir)) return;
            var locRoot = Path.Combine(libDir, "localization");
            if (!Directory.Exists(locRoot))
            {
                HostLog.Info($"no localization at {locRoot}; text falls back to entry keys");
                return;
            }

            var asm = typeof(MegaCrit.Sts2.Core.Models.AbstractModelSubtypes).Assembly;
            var locManagerType = asm.GetType("MegaCrit.Sts2.Core.Localization.LocManager")
                ?? throw new InvalidOperationException("LocManager type missing");
            var locTableType = asm.GetType("MegaCrit.Sts2.Core.Localization.LocTable")
                ?? throw new InvalidOperationException("LocTable type missing");
            var locTableCtor = locTableType.GetConstructors().FirstOrDefault(c =>
                    c.GetParameters().Length == 3
                    && c.GetParameters()[0].ParameterType == typeof(string)
                    && c.GetParameters()[1].ParameterType == typeof(Dictionary<string, string>))
                ?? throw new InvalidOperationException("LocTable(string, Dictionary, LocTable) ctor missing");

            var lang = Environment.GetEnvironmentVariable("STS2_AGENT_LANG");
            if (string.IsNullOrWhiteSpace(lang) || !Directory.Exists(Path.Combine(locRoot, lang)))
                lang = "eng";

            var eng = LoadTables(locRoot, locTableType, locTableCtor, "eng", null);
            var tables = lang == "eng" ? eng : LoadTables(locRoot, locTableType, locTableCtor, lang, eng);
            if (tables is null) return;

            // LocManager's ctor walks res:// — build an empty instance and
            // wire its fields directly.
            var lm = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(locManagerType);
            SetField(locManagerType, lm, "_tables", tables);
            SetBackingField(locManagerType, lm, "Language", lang);
            var culture = CultureFor(lang);
            SetBackingField(locManagerType, lm, "CultureInfo", culture);
            try { SetBackingField(locManagerType, lm, "StringComparer", StringComparer.Create(culture, false)); }
            catch { }
            try
            {
                var validationType = asm.GetType("MegaCrit.Sts2.Core.Localization.LocValidationError");
                if (validationType is not null)
                    SetBackingField(locManagerType, lm, "ValidationErrors", Array.CreateInstance(validationType, 0));
            }
            catch { }
            try
            {
                var fld = locManagerType.GetField("_localeChangeCallbacks",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (fld is not null) fld.SetValue(lm, Activator.CreateInstance(fld.FieldType));
            }
            catch { }

            // Instance must be live before LoadLocFormatters — the engine's
            // formatter setup (cond/plural/… SmartFormat extensions) reads it.
            locManagerType.GetProperty("Instance",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                ?.SetValue(null, lm);
            try
            {
                locManagerType.GetMethod("LoadLocFormatters",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Instance)?.Invoke(lm, null);
            }
            catch (Exception ex)
            {
                HostLog.Error("LoadLocFormatters", ex);
            }
            HostLog.Info($"localization: lang={lang}, {tables.Count} tables");
        }
        catch (Exception ex)
        {
            HostLog.Error("localization init failed", ex);
        }
    }

    private static System.Collections.IDictionary? LoadTables(
        string locRoot, Type locTableType, System.Reflection.ConstructorInfo locTableCtor,
        string lang, System.Collections.IDictionary? fallbackTables)
    {
        var langDir = Path.Combine(locRoot, lang);
        if (!Directory.Exists(langDir)) return null;
        var dictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), locTableType);
        var tables = (System.Collections.IDictionary)Activator.CreateInstance(dictType)!;
        var jsonOpts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var path in Directory.GetFiles(langDir, "*.json"))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var entries = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(path), jsonOpts) ?? new Dictionary<string, string>();
                var fallback = fallbackTables is not null && fallbackTables.Contains(name)
                    ? fallbackTables[name]
                    : null;
                tables[name] = locTableCtor.Invoke(new object?[] { name, entries, fallback });
            }
            catch (Exception ex)
            {
                HostLog.Error($"loc table {path}", ex);
            }
        }
        return tables;
    }

    private static System.Globalization.CultureInfo CultureFor(string lang) => lang switch
    {
        "eng" => System.Globalization.CultureInfo.GetCultureInfo("en"),
        "zhs" => System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),
        "fra" => System.Globalization.CultureInfo.GetCultureInfo("fr"),
        "deu" => System.Globalization.CultureInfo.GetCultureInfo("de"),
        "esp" or "spa" => System.Globalization.CultureInfo.GetCultureInfo("es"),
        "ita" => System.Globalization.CultureInfo.GetCultureInfo("it"),
        "jpn" => System.Globalization.CultureInfo.GetCultureInfo("ja"),
        "kor" => System.Globalization.CultureInfo.GetCultureInfo("ko"),
        "ptb" => System.Globalization.CultureInfo.GetCultureInfo("pt-BR"),
        "rus" => System.Globalization.CultureInfo.GetCultureInfo("ru"),
        "pol" => System.Globalization.CultureInfo.GetCultureInfo("pl"),
        "tha" => System.Globalization.CultureInfo.GetCultureInfo("th"),
        _ => System.Globalization.CultureInfo.InvariantCulture,
    };

    private static void SetField(Type t, object instance, string name, object? value) =>
        t.GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(instance, value);

    private static void SetBackingField(Type t, object instance, string prop, object? value)
    {
        var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
                 | System.Reflection.BindingFlags.Static;
        var fi = t.GetField($"<{prop}>k__BackingField", bf);
        if (fi is not null) { fi.SetValue(instance, value); return; }
        var pi = t.GetProperty(prop, bf);
        if (pi is { CanWrite: true }) pi.SetValue(instance, value);
    }
}

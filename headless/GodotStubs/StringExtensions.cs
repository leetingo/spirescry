namespace Godot;

// GodotSharp implements these over native string functions. Only members
// sts2 calls need exact behavior; the rest are best-effort stand-ins.
public static class StringExtensions
{
    public static string Capitalize(this string instance)
    {
        var s = instance.Replace("_", " ");
        return string.Join(" ", s.Split(' ').Select(w =>
            w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }

    public static string ToSnakeCase(this string instance) =>
        string.Concat(instance.Select((c, i) =>
            char.IsUpper(c) ? (i > 0 ? "_" : "") + char.ToLowerInvariant(c) : c.ToString()));

    public static string ToCamelCase(this string instance)
    {
        var p = instance.ToPascalCase();
        return p.Length == 0 ? p : char.ToLowerInvariant(p[0]) + p[1..];
    }

    public static string ToPascalCase(this string instance) =>
        string.Concat(instance.Split('_', ' ').Where(w => w.Length > 0)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));

    public static string GetBaseDir(this string instance)
    {
        var i = instance.LastIndexOf('/');
        return i < 0 ? "" : instance[..i];
    }

    public static string GetFile(this string instance)
    {
        var i = instance.LastIndexOf('/');
        return i < 0 ? instance : instance[(i + 1)..];
    }

    public static string GetBaseName(this string instance)
    {
        var i = instance.LastIndexOf('.');
        return i < 0 ? instance : instance[..i];
    }

    public static string GetExtension(this string instance)
    {
        var i = instance.LastIndexOf('.');
        return i < 0 ? instance : instance[(i + 1)..];
    }

    public static bool IsAbsolutePath(this string instance) =>
        instance.StartsWith('/') || instance.Contains(":/") || instance.Contains(":\\");

    public static bool IsRelativePath(this string instance) => !instance.IsAbsolutePath();

    public static string PathJoin(this string instance, string file) =>
        instance.EndsWith('/') ? instance + file : instance + "/" + file;

    public static string SimplifyPath(this string instance) => instance;

    public static string TrimPrefix(this string instance, string prefix) =>
        instance.StartsWith(prefix) ? instance[prefix.Length..] : instance;

    public static string TrimSuffix(this string instance, string suffix) =>
        instance.EndsWith(suffix) ? instance[..^suffix.Length] : instance;

    public static string PadZeros(this string instance, int digits)
    {
        var neg = instance.StartsWith('-');
        var body = neg ? instance[1..] : instance;
        return (neg ? "-" : "") + body.PadLeft(digits, '0');
    }

    // Godot's 32-bit string hash (djb2 variant) must match the engine.
    public static uint Hash(this string instance)
    {
        uint h = 5381;
        foreach (var c in instance) h = (h << 5) + h + c;
        return h;
    }

    public static bool IsValidFileName(this string instance) =>
        instance.Length > 0 && instance.Trim() == instance
        && !instance.Any(c => c is ':' or '/' or '\\' or '?' or '*' or '"' or '|' or '%' or '<' or '>');

    public static bool IsValidIdentifier(this string instance) =>
        instance.Length > 0 && !char.IsDigit(instance[0])
        && instance.All(c => c == '_' || char.IsLetterOrDigit(c));

    public static string CEscape(this string instance) =>
        instance.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t");

    public static string CUnescape(this string instance) =>
        instance.Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\"", "\"").Replace("\\\\", "\\");

    public static string Left(this string instance, int position) =>
        position <= 0 ? "" : position >= instance.Length ? instance : instance[..position];

    public static string Right(this string instance, int position) =>
        position >= instance.Length ? "" : instance[position..];
}

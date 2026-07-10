using System.Text.RegularExpressions;

namespace Spirescry.State;

// Godot rich text reaches snapshots with [img]res://…[/img] icon tags —
// resource paths carry nothing for an agent. Collapse each icon to a
// stable, readable token: the file name minus extension and _icon suffix.
// Energy icons are per-character (ironclad_energy, silent_energy, …), so
// any *_energy collapses to one [energy] token.
internal static class RichText
{
    private static readonly Regex Img = new(
        @"\[img[^\]]*\](?<path>[^\[]*)\[/img\]", RegexOptions.Compiled);

    public static string NormalizeIcons(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("[img")) return text;
        return Img.Replace(text, m =>
        {
            var name = m.Groups["path"].Value;
            var slash = name.LastIndexOf('/');
            if (slash >= 0) name = name[(slash + 1)..];
            var dot = name.LastIndexOf('.');
            if (dot >= 0) name = name[..dot];
            if (name.EndsWith("_icon")) name = name[..^"_icon".Length];
            if (name.EndsWith("_energy")) name = "energy";
            return name.Length == 0 ? "" : $"[{name}]";
        });
    }
}

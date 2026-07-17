using System.Reflection;

namespace Spirescry.Host;

internal static class FirstChanceFilter
{
    private static readonly string[] KnownMissingGodotTypes =
    [
        "MethodName",
        "PropertyName",
        "SignalName",
        "FocusBehaviorRecursiveEnum",
        "EventType",
        "Godot.Collections.Dictionary",
    ];

    private const string KnownArrayEnumeratorMiss =
        "Method not found: 'System.Collections.Generic.IEnumerator`1<!0> "
        + "Godot.Collections.Array`1.GetEnumerator()'.";

    internal static bool IsKnownGodotStubMiss(Exception ex) => ex switch
    {
        ReflectionTypeLoadException rtl =>
            rtl.LoaderExceptions is { Length: > 0 }
            && rtl.LoaderExceptions.All(loader =>
                loader is TypeLoadException typeLoad && IsKnownTypeLoad(typeLoad)),
        TypeLoadException typeLoad => IsKnownTypeLoad(typeLoad),
        MissingMethodException missing =>
            string.Equals(missing.Message, KnownArrayEnumeratorMiss, StringComparison.Ordinal),
        _ => false,
    };

    private static bool IsKnownTypeLoad(TypeLoadException ex) =>
        KnownMissingGodotTypes.Any(typeName => ex.Message.StartsWith(
            $"Could not load type '{typeName}' from assembly 'GodotSharp, ",
            StringComparison.Ordinal));
}

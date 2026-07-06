using System.Collections.Concurrent;
using System.Reflection;

namespace Spirescry;

// The reward overlays expose no public API — their tiles and handlers are
// private members of the screen nodes. Invoking the screen's own button
// handlers keeps us on the exact code path the UI uses.
//
// Lookups walk the type hierarchy (GetField/GetProperty skip private
// members declared on base types) and run on every bridge request, so
// each resolved member is cached per (type, name).
internal static class Reflect
{
    private const BindingFlags Flags =
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static readonly ConcurrentDictionary<(Type, string), FieldInfo?> Fields = new();
    private static readonly ConcurrentDictionary<(Type, string), MethodInfo?> Getters = new();
    private static readonly ConcurrentDictionary<(Type, string), MethodInfo?> Setters = new();
    private static readonly ConcurrentDictionary<(Type, string, int), MethodInfo?> Methods = new();

    private static FieldInfo? FindField(Type type, string name) =>
        Fields.GetOrAdd((type, name), static key =>
        {
            for (var t = key.Item1; t is not null; t = t.BaseType)
                if (t.GetField(key.Item2, Flags) is { } f)
                    return f;
            return null;
        });

    public static T? Field<T>(object obj, string name) where T : class =>
        FieldValue(obj, name) as T;

    public static object? FieldValue(object obj, string name) =>
        FindField(obj.GetType(), name)?.GetValue(obj);

    public static object? PropertyValue(object obj, string name) =>
        Getters.GetOrAdd((obj.GetType(), name), static key =>
        {
            for (var t = key.Item1; t is not null; t = t.BaseType)
                if (t.GetProperty(key.Item2, Flags)?.GetGetMethod(true) is { } get)
                    return get;
            return null;
        })?.Invoke(obj, null);

    // Some engine properties (Creature.CurrentHp, …) have private setters;
    // the setter path still runs the property's change events.
    public static bool SetProperty(object obj, string name, object? value)
    {
        var set = Setters.GetOrAdd((obj.GetType(), name), static key =>
        {
            for (var t = key.Item1; t is not null; t = t.BaseType)
                if (t.GetProperty(key.Item2, Flags)?.GetSetMethod(true) is { } s)
                    return s;
            return null;
        });
        if (set is null) return false;
        set.Invoke(obj, new[] { value });
        return true;
    }

    public static bool SetField(object obj, string name, object? value)
    {
        if (FindField(obj.GetType(), name) is not { } f) return false;
        f.SetValue(obj, value);
        return true;
    }

    // For get-only auto-properties the setter path fails — fall back to
    // the compiler's backing field.
    public static bool SetPropertyOrBackingField(object obj, string name, object? value) =>
        SetProperty(obj, name, value) || SetField(obj, $"<{name}>k__BackingField", value);

    public static object? Invoke(object obj, string name, params object?[] args)
    {
        var m = Methods.GetOrAdd((obj.GetType(), name, args.Length), static key =>
        {
            for (var t = key.Item1; t is not null; t = t.BaseType)
            {
                var hit = t.GetMethods(Flags).FirstOrDefault(m =>
                    m.Name == key.Item2 && m.GetParameters().Length == key.Item3);
                if (hit is not null) return hit;
            }
            return null;
        });
        if (m is null) throw new MissingMethodException(obj.GetType().Name, name);
        return m.Invoke(obj, args);
    }
}

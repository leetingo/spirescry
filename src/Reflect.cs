using System.Reflection;

namespace Spirescry;

// The reward overlays expose no public API — their tiles and handlers are
// private members of the screen nodes. Invoking the screen's own button
// handlers keeps us on the exact code path the UI uses.
internal static class Reflect
{
    private const BindingFlags Flags =
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static T? Field<T>(object obj, string name) where T : class
    {
        for (var t = obj.GetType(); t is not null; t = t.BaseType)
            if (t.GetField(name, Flags) is { } f)
                return f.GetValue(obj) as T;
        return null;
    }

    public static object? FieldValue(object obj, string name)
    {
        for (var t = obj.GetType(); t is not null; t = t.BaseType)
            if (t.GetField(name, Flags) is { } f)
                return f.GetValue(obj);
        return null;
    }

    public static object? PropertyValue(object obj, string name)
    {
        for (var t = obj.GetType(); t is not null; t = t.BaseType)
            if (t.GetProperty(name, Flags) is { } p && p.GetGetMethod(true) is { } get)
                return get.Invoke(obj, null);
        return null;
    }

    // Some engine properties (Creature.CurrentHp, …) have private setters;
    // the setter path still runs the property's change events.
    public static bool SetProperty(object obj, string name, object? value)
    {
        for (var t = obj.GetType(); t is not null; t = t.BaseType)
            if (t.GetProperty(name, Flags) is { } p && p.GetSetMethod(true) is { } set)
            {
                set.Invoke(obj, new[] { value });
                return true;
            }
        return false;
    }

    public static bool SetField(object obj, string name, object? value)
    {
        for (var t = obj.GetType(); t is not null; t = t.BaseType)
            if (t.GetField(name, Flags) is { } f)
            {
                f.SetValue(obj, value);
                return true;
            }
        return false;
    }

    public static object? Invoke(object obj, string name, params object?[] args)
    {
        for (var t = obj.GetType(); t is not null; t = t.BaseType)
        {
            var m = t.GetMethods(Flags).FirstOrDefault(m =>
                m.Name == name && m.GetParameters().Length == args.Length);
            if (m is not null) return m.Invoke(obj, args);
        }
        throw new MissingMethodException(obj.GetType().Name, name);
    }
}

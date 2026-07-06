namespace Godot.Bridge;

public static class ScriptManagerBridge
{
    public static void FrameworkGetGodotMethodList(IntPtr handle) { }
}

public static class CSharpInstanceBridge { }

// Moved here from Types.cs (Godot top-level) so they don't clash with
// System.Reflection.MethodInfo / System.Reflection.PropertyInfo when both
// namespaces are imported. The real GodotSharp puts them here.

public struct PropertyInfo
{
    public Variant.Type Type;
    public StringName Name;
    public PropertyHint Hint;
    public string HintString;
    public PropertyUsageFlags Usage;
    public bool Exported;

    public PropertyInfo(Variant.Type type, StringName name, PropertyHint hint = PropertyHint.None,
        string hintString = "", PropertyUsageFlags usage = PropertyUsageFlags.Default, bool exported = false)
    {
        Type = type; Name = name; Hint = hint; HintString = hintString; Usage = usage; Exported = exported;
    }
}

public struct MethodInfo
{
    public StringName Name;
    public PropertyInfo ReturnVal;
    public MethodFlags Flags;
    public List<PropertyInfo>? DefaultArguments;
    public List<PropertyInfo>? Arguments;

    public MethodInfo(StringName name, PropertyInfo returnVal, MethodFlags flags,
        List<PropertyInfo>? arguments, List<PropertyInfo>? defaultArguments)
    {
        Name = name; ReturnVal = returnVal; Flags = flags;
        Arguments = arguments; DefaultArguments = defaultArguments;
    }
}

public class GodotSerializationInfo
{
    public void AddProperty(string name, Variant value) { }
    public bool TryGetProperty(string name, out Variant value) { value = default; return false; }
}

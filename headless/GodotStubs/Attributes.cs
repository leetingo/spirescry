namespace Godot;

[AttributeUsage(AttributeTargets.Class)]
public class ToolAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class ExportToolButtonAttribute : Attribute
{
    public ExportToolButtonAttribute(string text, string icon = "") { }
}

[AttributeUsage(AttributeTargets.Assembly)]
public class AssemblyHasScriptsAttribute : Attribute
{
    public AssemblyHasScriptsAttribute() { }
    public AssemblyHasScriptsAttribute(string[] scripts) { }

    // The real Godot SDK emits `[assembly: AssemblyHasScripts(new Type[]{…})]`
    // into sts2.dll. Reflection-heavy hosts materialize that attribute and
    // need a matching Type[] ctor; the headless executable never reflects it.
    public AssemblyHasScriptsAttribute(Type[] scriptTypes) { }
}

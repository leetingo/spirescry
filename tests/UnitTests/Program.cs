using System.Reflection;
using Spirescry;

// Every public static parameterless method on Tests is a test — discovered
// here by reflection so a new test can't be silently left unregistered.
var tests = typeof(Tests)
    .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
    .Where(m => m.GetParameters().Length == 0 && !m.IsGenericMethod)
    .ToArray();

if (tests.Length == 0)
{
    Console.Error.WriteLine("not ok - no tests discovered");
    return 1;
}

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Invoke(null, null);
        Console.WriteLine($"ok - {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
        Console.Error.WriteLine($"not ok - {test.Name}: {cause.Message}");
    }
}

return failures == 0 ? 0 : 1;

internal static class Tests
{
    public static void FieldValueFindsPrivateFieldsDeclaredOnBaseTypes()
    {
        var target = new DerivedProbe();

        Equal("base-secret", Reflect.FieldValue(target, "_secret"));
    }

    public static void PropertyValueInvokesPrivateGettersDeclaredOnBaseTypes()
    {
        var target = new DerivedProbe();

        Equal("computed-value", Reflect.PropertyValue(target, "Computed"));
    }

    public static void SetPropertyInvokesPrivateSetters()
    {
        var target = new DerivedProbe();

        True(Reflect.SetProperty(target, "Mutable", "changed"));

        Equal("changed", target.ReadMutable());
    }

    public static void SetPropertyOrBackingFieldSetsGetOnlyAutoProperties()
    {
        var target = new DerivedProbe();

        True(Reflect.SetPropertyOrBackingField(target, "GetOnly", "patched"));

        Equal("patched", target.GetOnly);
    }

    public static void InvokeFindsPrivateMethodsDeclaredOnBaseTypes()
    {
        var target = new DerivedProbe();

        Equal("left:right", Reflect.Invoke(target, "Join", "left", "right"));
    }

    public static void InvokeReportsMissingMethods()
    {
        var target = new DerivedProbe();

        Throws<MissingMethodException>(() => Reflect.Invoke(target, "Missing"));
    }

    private static void Equal(object? expected, object? actual)
    {
        if (!Equals(expected, actual))
            throw new InvalidOperationException($"expected {expected ?? "<null>"}, got {actual ?? "<null>"}");
    }

    private static void True(bool actual)
    {
        if (!actual)
            throw new InvalidOperationException("expected true");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"expected {typeof(T).Name}");
    }
}

internal class BaseProbe
{
    private readonly string _secret = "base-secret";

    private string Computed => "computed-value";

    public string GetOnly { get; } = "initial";

    private string Mutable { get; set; } = "initial";

    public string ReadSecret() => _secret;

    public string ReadMutable() => Mutable;

    private string Join(string first, string second) => $"{first}:{second}";
}

internal sealed class DerivedProbe : BaseProbe
{
}

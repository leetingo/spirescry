using Spirescry;

var tests = new (string Name, Action Run)[]
{
    ("FieldValue finds private fields declared on base types", FieldValueFindsBasePrivateField),
    ("PropertyValue invokes private getters declared on base types", PropertyValueFindsBasePrivateGetter),
    ("SetProperty invokes private setters", SetPropertyInvokesPrivateSetter),
    ("SetPropertyOrBackingField sets get-only auto-properties", SetPropertyOrBackingFieldSetsGetOnlyProperty),
    ("Invoke finds private methods declared on base types", InvokeFindsBasePrivateMethod),
    ("Invoke reports missing methods", InvokeReportsMissingMethods),
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"ok - {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"not ok - {test.Name}: {ex.Message}");
    }
}

return failures == 0 ? 0 : 1;

static void FieldValueFindsBasePrivateField()
{
    var target = new DerivedProbe();

    Equal("base-secret", Reflect.FieldValue(target, "_secret"));
}

static void PropertyValueFindsBasePrivateGetter()
{
    var target = new DerivedProbe();

    Equal("computed-value", Reflect.PropertyValue(target, "Computed"));
}

static void SetPropertyInvokesPrivateSetter()
{
    var target = new DerivedProbe();

    True(Reflect.SetProperty(target, "Mutable", "changed"));

    Equal("changed", target.ReadMutable());
}

static void SetPropertyOrBackingFieldSetsGetOnlyProperty()
{
    var target = new DerivedProbe();

    True(Reflect.SetPropertyOrBackingField(target, "GetOnly", "patched"));

    Equal("patched", target.GetOnly);
}

static void InvokeFindsBasePrivateMethod()
{
    var target = new DerivedProbe();

    Equal("left:right", Reflect.Invoke(target, "Join", "left", "right"));
}

static void InvokeReportsMissingMethods()
{
    var target = new DerivedProbe();

    Throws<MissingMethodException>(() => Reflect.Invoke(target, "Missing"));
}

static void Equal(object? expected, object? actual)
{
    if (!Equals(expected, actual))
        throw new InvalidOperationException($"expected {expected ?? "<null>"}, got {actual ?? "<null>"}");
}

static void True(bool actual)
{
    if (!actual)
        throw new InvalidOperationException("expected true");
}

static void Throws<T>(Action action) where T : Exception
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

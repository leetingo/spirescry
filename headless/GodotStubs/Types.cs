// Audited from sts2-cli/src/GodotStubs/Types.cs.
// Status: clean apart from Variant.As<T> and Callable.Call. See risk notes.

using System.Runtime.CompilerServices;

namespace Godot;

// StringName wraps string. Must be a class (not struct) to match real Godot's
// declaration — sts2 references StringName via class-typed fields/parameters.
public sealed class StringName : IDisposable
{
    private readonly string? _name;
    public StringName() => _name = "";
    public StringName(string name) => _name = name;
    public static implicit operator StringName(string s) => new(s);
    public static implicit operator string(StringName s) => s?._name ?? "";
    public override string ToString() => _name ?? "";
    public override int GetHashCode() => (_name ?? "").GetHashCode();
    public override bool Equals(object? obj) => obj switch
    {
        StringName sn => _name == sn._name,
        string s => _name == s,
        _ => false
    };
    public static bool operator ==(StringName? a, StringName? b) => (a?._name ?? "") == (b?._name ?? "");
    public static bool operator !=(StringName? a, StringName? b) => !(a == b);
    public void Dispose() { }
}

public sealed class NodePath : IDisposable
{
    private readonly string _path;
    public NodePath() => _path = "";
    public NodePath(string path) => _path = path;
    public static implicit operator NodePath(string s) => new(s);
    public static implicit operator string(NodePath p) => p?._path ?? "";
    public override string ToString() => _path ?? "";
    public void Dispose() { }
}

public struct Variant
{
    public enum Type
    {
        Nil, Bool, Int, Float, String, Vector2, Vector2I, Rect2, Vector3,
        Transform2D, Color, StringName, NodePath, Object, Dictionary, Array, Signal, Callable
    }

    private readonly object? _value;
    public Variant(object? value) => _value = value;

    public static Variant From<T>(T value) => new(value);
    public static Variant CreateFrom<T>(T value) => new(value);

    // Risk: As<T>() casts directly. If the stored value's runtime type
    // doesn't match T (e.g. variant holds long, code asks for int), this
    // throws InvalidCastException. Real Godot does numeric coercion. So
    // far sts2 hot paths haven't tripped this — leave it strict; relax
    // if a specific call site bites.
    public T As<T>() => (T)_value!;
    public object? Obj => _value;

    public static implicit operator Variant(bool v) => new(v);
    public static implicit operator Variant(int v) => new(v);
    public static implicit operator Variant(long v) => new(v);
    public static implicit operator Variant(float v) => new(v);
    public static implicit operator Variant(double v) => new(v);
    public static implicit operator Variant(string v) => new(v);
    public static implicit operator Variant(StringName v) => new(v);
    public static implicit operator Variant(GodotObject v) => new(v);
    public static implicit operator Variant(Vector2 v) => new(v);
    public static implicit operator Variant(Color v) => new(v);

    public static implicit operator bool(Variant v) => v._value is bool b ? b : false;
    public static implicit operator int(Variant v) => v._value is int i ? i : 0;
    public static implicit operator long(Variant v) => v._value is long l ? l : 0;
    public static implicit operator float(Variant v) => v._value is float f ? f : 0f;
    public static implicit operator double(Variant v) => v._value is double d ? d : 0;
    public static implicit operator string(Variant v) => v._value?.ToString() ?? "";
}

// Callable wraps a delegate. The original sts2-cli stub only invoked Action
// (zero-arg) and silently dropped multi-arg signatures. We dispatch via
// Delegate.DynamicInvoke instead so any Action<T...> or Func<T...> stored in
// the Callable actually fires when Call(args) is reached.
public struct Callable
{
    private readonly Delegate? _delegate;
    public Callable(Delegate? d) => _delegate = d;

    public static Callable From(Action action) => new(action);
    public static Callable From<T>(Action<T> action) => new(action);
    public static Callable From<T1, T2>(Action<T1, T2> action) => new(action);

    public void Call(params Variant[] args)
    {
        if (_delegate is null) return;
        try
        {
            // Convert Variant[] → object?[] matching the delegate's parameters.
            var pars = _delegate.Method.GetParameters();
            var converted = new object?[pars.Length];
            for (int i = 0; i < pars.Length && i < args.Length; i++)
                converted[i] = args[i].Obj;
            _delegate.DynamicInvoke(converted);
        }
        catch
        {
            // Swallow: a Callable invoked with a wrong arg count is fairly
            // benign in headless (we'd rather drop the signal than crash).
        }
    }

    public void CallDeferred(params Variant[] args) => Call(args);
}

[AttributeUsage(AttributeTargets.Delegate)]
public class SignalAttribute : Attribute { }

public interface IAwaiter : INotifyCompletion
{
    bool IsCompleted { get; }
    void GetResult();
}

public interface IAwaiter<out T> : INotifyCompletion
{
    bool IsCompleted { get; }
    T GetResult();
}

// SignalAwaiter — always reports IsCompleted = true so `await ToSignal(...)`
// resolves immediately. Game code that uses the await pattern for animation
// completion never blocks. This is a deliberate semantic — every animation
// is "already finished" in headless mode.
public class SignalAwaiter : IAwaiter<Variant[]>
{
    private bool _completed = true;
    private Action? _continuation;

    public IAwaiter<Variant[]> GetAwaiter() => this;
    public bool IsCompleted => _completed;
    public Variant[] GetResult() => Array.Empty<Variant>();
    public void OnCompleted(Action continuation)
    {
        if (_completed) continuation();
        else _continuation = continuation;
    }

    internal void Complete()
    {
        _completed = true;
        _continuation?.Invoke();
    }
}

public enum PropertyHint
{
    None, Range, Enum, ResourceType, NodeType, TypeString, File, Dir, GlobalFile, GlobalDir
}

[Flags]
public enum PropertyUsageFlags
{
    None = 0,
    Default = 1,
    ScriptVariable = 2,
    Storage = 4,
    Editor = 8
}

public enum MethodFlags { Normal, Editor, Virtual }

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class ExportAttribute : Attribute
{
    public ExportAttribute() { }
    public ExportAttribute(PropertyHint hint, string hintString = "") { }
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class ExportGroupAttribute : Attribute
{
    public ExportGroupAttribute(string name, string prefix = "") { }
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class ExportCategoryAttribute : Attribute
{
    public ExportCategoryAttribute(string name) { }
}

[AttributeUsage(AttributeTargets.Class)]
public class ScriptPathAttribute : Attribute
{
    public ScriptPathAttribute(string path) { }
}

[AttributeUsage(AttributeTargets.Class)]
public class GlobalClassAttribute : Attribute { }

// PropertyInfo / MethodInfo / GodotSerializationInfo live under
// Godot.Bridge in the real GodotSharp — the source generator emits
// declarations against this namespace. Putting them under Godot top
// level (sts2-cli's stub did) clashes with System.Reflection.MethodInfo
// when source files have `using Godot; using System.Reflection;`.

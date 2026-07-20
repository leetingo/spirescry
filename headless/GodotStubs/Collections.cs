namespace Godot.Collections;

// Godot Array<T> wrapper
public class Array<T> : List<T>
{
    public Array() { }
    public Array(IEnumerable<T> items) : base(items) { }

    // Real Godot declares GetEnumerator returning IEnumerator<T>; the
    // inherited List<T> version returns a struct enumerator, so game IL
    // compiled against the real signature can't JIT without this (the
    // "KnownArrayEnumeratorMiss" in FirstChanceFilter — and unpatchable
    // methods for Harmony, e.g. NEventLayout.RemoveNodesOnPortrait).
    public new IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)this).GetEnumerator();
}

// Godot Dictionary
public class Dictionary<TKey, TValue> : System.Collections.Generic.Dictionary<TKey, TValue>
    where TKey : notnull
{
    public Dictionary() { }
}

// Non-generic Array
public class Array : List<Variant>
{
    public Array() { }
}

namespace Godot;

public enum Key
{
    None, A, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    Escape, Enter, Tab, Space, Left, Right, Up, Down,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
}

public enum MouseButton { None, Left, Right, Middle, WheelUp, WheelDown }

public class InputEventJoypadMotion : InputEvent
{
    public int Axis { get; set; }
    public float AxisValue { get; set; }
}

public class InputEventAction : InputEvent
{
    public StringName Action { get; set; } = "";
}

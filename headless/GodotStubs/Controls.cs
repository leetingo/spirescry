namespace Godot;

public class NinePatchRect : Control
{
    public Texture2D? Texture { get; set; }
}

public class AspectRatioContainer : Container { }
public class VFlowContainer : FlowContainer { }

public class Font : Resource
{
    public float GetStringSize(
        string text, int alignment = 0, float width = -1, int fontSize = 16) =>
        text.Length * fontSize * 0.6f;
}

public class TextParagraph
{
    public void Clear() { }
    public void AddString(string text, Font font, int fontSize) { }
    public Vector2 GetSize() => Vector2.Zero;
    public float GetWidth() => 0;
}

public class StyleBoxEmpty : Resource { }

public class Range : Control
{
    public new class SignalName : Control.SignalName
    {
        public static readonly StringName ValueChanged = "ValueChanged";
    }

    public double Value { get; set; }
    public double MinValue { get; set; }
    public double MaxValue { get; set; } = 100;
    public double Step { get; set; } = 1;
    public double Page { get; set; }
    public float Ratio { get; set; }
}

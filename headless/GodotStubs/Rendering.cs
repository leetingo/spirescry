namespace Godot;

public class Line2D : Node2D
{
    public Vector2[] Points { get; set; } = Array.Empty<Vector2>();
    public float Width { get; set; } = 1f;
    public Color DefaultColor { get; set; } = Color.White;
    public void AddPoint(Vector2 position, int? atPosition = null) { }
    public void ClearPoints() { }
}

public class CpuParticles2D : Node2D
{
    public bool Emitting { get; set; }
}

public class Marker2D : Node2D { }

public class PathFollow2D : Node2D
{
    public float Progress { get; set; }
    public float ProgressRatio { get; set; }
}

public class Path2D : Node2D { }
public class BackBufferCopy : Node2D { }
public class CanvasGroup : Node2D { }
public class CanvasItemMaterial : Material { }
public class WorldEnvironment : Node { }
public class FastNoiseLite : Resource { }
public class GradientTexture2D : Texture2D { }
public class Gradient : Resource { }

public class ParticleProcessMaterial : Material
{
    public Vector3 EmissionBoxExtents { get; set; }
}

public class RenderingServer
{
    public enum ViewportMsaa { Disabled, Msaa2X, Msaa4X, Msaa8X }
    public static void GlobalShaderParameterSet(StringName name, Variant value) { }
}

public static class Colors
{
    public static Color White { get; } = Color.White;
    public static Color Black { get; } = Color.Black;
    public static Color Red { get; } = new(1, 0, 0);
    public static Color Green { get; } = new(0, 1, 0);
    public static Color Blue { get; } = new(0, 0, 1);
    public static Color Yellow { get; } = new(1, 1, 0);
    public static Color Transparent { get; } = Color.Transparent;
    public static Color Orange { get; } = new(1, 0.65f, 0);
    public static Color Purple { get; } = new(0.63f, 0.13f, 0.94f);
    public static Color Cyan { get; } = new(0, 1, 1);
    public static Color Magenta { get; } = new(1, 0, 1);
    public static Color Gray { get; } = new(0.5f, 0.5f, 0.5f);
    public static Color DarkGray { get; } = new(0.25f, 0.25f, 0.25f);
    public static Color LightGray { get; } = new(0.75f, 0.75f, 0.75f);
}

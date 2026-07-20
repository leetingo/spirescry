using System.Text.Json.Nodes;

namespace Spirescry;

// The health projection of protocol vocabulary. Keep the legacy name list
// for older callers and advertise the artifact's argument shapes beside it.
public static class ProtocolCapabilities
{
    public static object Create(IReadOnlyList<string> verbs) => new
    {
        verbs,
        cheats = ProtocolVocabulary.Cheats.All.Select(shape => shape.Name),
        cheatArgumentShapes = new JsonArray(ProtocolVocabulary.Cheats.All
            .Select(ShapeNode).ToArray()),
    };

    private static JsonNode ShapeNode(CheatArgumentShape shape) => new JsonObject
    {
        ["name"] = shape.Name,
        ["arguments"] = new JsonArray(shape.Arguments.Select(ArgumentNode).ToArray()),
    };

    private static JsonNode ArgumentNode(ProtocolArgument argument)
    {
        var node = new JsonObject
        {
            ["name"] = argument.Name,
            ["type"] = argument.Type.ToString().ToLowerInvariant(),
        };
        if (argument.Optional) node["optional"] = true;
        return node;
    }
}

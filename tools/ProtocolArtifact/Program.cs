using System.Text;
using Spirescry;

string? checkPath = null;
string? outputPath = null;

for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--check" when index + 1 < args.Length:
            checkPath = args[++index];
            break;
        case "--output" when index + 1 < args.Length:
            outputPath = args[++index];
            break;
        default:
            return Usage($"unknown or incomplete argument: {args[index]}");
    }
}

if (checkPath is null && outputPath is null)
    return Usage("pass --check <path>, --output <path>, or both");

var generated = ProtocolVocabulary.CreateArtifactJson();
if (checkPath is not null)
{
    if (!File.Exists(checkPath))
    {
        Console.Error.WriteLine($"protocol artifact is missing: {checkPath}");
        return 1;
    }

    var checkedArtifact = File.ReadAllText(checkPath, Encoding.UTF8);
    if (!string.Equals(checkedArtifact, generated, StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            $"protocol artifact drifted from ProtocolVocabulary: {checkPath}");
        Console.Error.WriteLine(
            "regenerate it with: dotnet run --project tools/ProtocolArtifact -- --output protocol.json");
        return 1;
    }
}

if (outputPath is not null)
{
    var fullOutputPath = Path.GetFullPath(outputPath);
    Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
    File.WriteAllText(fullOutputPath, generated, new UTF8Encoding(false));
}

return 0;

static int Usage(string message)
{
    Console.Error.WriteLine(message);
    Console.Error.WriteLine(
        "usage: ProtocolArtifact [--check <artifact>] [--output <artifact>]");
    return 2;
}

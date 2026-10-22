using System.Reflection;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: WakeWordModelExtractor <NanoWakeWord.dll> <output-directory>");
    return 2;
}

var assemblyPath = Path.GetFullPath(args[0]);
var outputDirectory = Path.GetFullPath(args[1]);
if (!File.Exists(assemblyPath))
{
    Console.Error.WriteLine($"NanoWakeWord assembly not found: {assemblyPath}");
    return 2;
}

var assembly = Assembly.LoadFrom(assemblyPath);
if (!string.Equals(assembly.GetName().Name, "NanoWakeWord", StringComparison.Ordinal))
{
    Console.Error.WriteLine("The supplied assembly is not NanoWakeWord.");
    return 2;
}

const string resourcePrefix = "NanoWakeWord.Resources.models.";
string[] requiredModels =
[
    "alexa_v0.1.onnx",
    "embedding_model.onnx",
    "hey_jarvis_v0.1.onnx",
    "hey_marvin_v0.1.onnx",
    "hey_mycroft_v0.1.onnx",
    "melspectrogram.onnx"
];

var resources = assembly.GetManifestResourceNames().ToHashSet(StringComparer.Ordinal);
var missing = requiredModels
    .Where(model => !resources.Contains(resourcePrefix + model))
    .ToArray();
if (missing.Length > 0)
{
    Console.Error.WriteLine("NanoWakeWord is missing required embedded models: " + string.Join(", ", missing));
    return 3;
}

Directory.CreateDirectory(outputDirectory);
foreach (var model in requiredModels)
{
    var resourceName = resourcePrefix + model;
    var targetPath = Path.Combine(outputDirectory, model);
    var temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
        using var source = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException($"Embedded model could not be opened: {resourceName}");
        using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            source.CopyTo(destination);
            destination.Flush(flushToDisk: true);
            if (destination.Length == 0)
                throw new InvalidDataException($"Embedded model is empty: {resourceName}");
        }

        File.Move(temporaryPath, targetPath, overwrite: true);
    }
    finally
    {
        if (File.Exists(temporaryPath))
            File.Delete(temporaryPath);
    }
}

Console.WriteLine($"Extracted and verified {requiredModels.Length} NanoWakeWord models into {outputDirectory}.");
return 0;

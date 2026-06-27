using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using DependencyFreezer;

return await ProgramEntryPoint.RunAsync(args).ConfigureAwait(false);

internal static class ProgramEntryPoint
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var command = args[0].ToLowerInvariant();
        var options = ParseOptions(args.Skip(1));
        var projectPath = options.TryGetValue("project", out var project) ? project.Single() : Directory.GetCurrentDirectory();
        IReadOnlyCollection<string> packageNames = options.TryGetValue("package", out var package) ? package : Array.Empty<string>();

        using var engine = new DependencyFreezerEngine();
        try
        {
            switch (command)
            {
                case "freeze":
                {
                    var result = await engine.FreezeAsync(new FreezeRequest(projectPath, packageNames.Count == 0 ? null : packageNames)).ConfigureAwait(false);
                    Console.WriteLine(JsonSerializer.Serialize(result, DependencyFreezerJson.SerializerOptions));
                    return 0;
                }
                case "unfreeze-preview":
                {
                    var preview = await engine.PreviewUnfreezeAsync(new UnfreezeRequest(projectPath, packageNames.Count == 0 ? null : packageNames, options.ContainsKey("all"))).ConfigureAwait(false);
                    Console.WriteLine(JsonSerializer.Serialize(preview, DependencyFreezerJson.SerializerOptions));
                    return preview.CanProceed ? 0 : 2;
                }
                case "unfreeze":
                {
                    var result = await engine.UnfreezeAsync(new UnfreezeRequest(projectPath, packageNames.Count == 0 ? null : packageNames, options.ContainsKey("all"))).ConfigureAwait(false);
                    Console.WriteLine(JsonSerializer.Serialize(result, DependencyFreezerJson.SerializerOptions));
                    return 0;
                }
                case "validate":
                {
                    var result = await engine.ValidateAsync(projectPath).ConfigureAwait(false);
                    Console.WriteLine(JsonSerializer.Serialize(result, DependencyFreezerJson.SerializerOptions));
                    return result.Success ? 0 : 3;
                }
                case "inspect":
                {
                    var result = await engine.InspectAsync(projectPath).ConfigureAwait(false);
                    Console.WriteLine(JsonSerializer.Serialize(result, DependencyFreezerJson.SerializerOptions));
                    return 0;
                }
                default:
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static Dictionary<string, List<string>> ParseOptions(IEnumerable<string> args)
    {
        var options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using var enumerator = args.GetEnumerator();
        while (enumerator.MoveNext())
        {
            var current = enumerator.Current;
            if (!current.StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected argument '{current}'.");
            }

            var key = current[2..];
            if (string.Equals(key, "all", StringComparison.OrdinalIgnoreCase))
            {
                options[key] = new List<string> { "true" };
                continue;
            }

            if (!enumerator.MoveNext())
            {
                throw new InvalidOperationException($"Missing value for option '{current}'.");
            }

            if (!options.TryGetValue(key, out var values))
            {
                values = new List<string>();
                options[key] = values;
            }

            values.Add(enumerator.Current);
        }

        return options;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("dependency-freezer <freeze|unfreeze-preview|unfreeze|validate|inspect> [--project <path>] [--package <name>] [--all]");
    }
}

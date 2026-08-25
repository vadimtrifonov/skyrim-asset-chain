namespace Skyrim.AssetChain;

internal static class CommandLine
{
    internal static bool IsHelp(IReadOnlyList<string> args) =>
        args.Count == 1 && args[0] is "--help" or "-h";

    internal static CommandOptions ParseOptions(string[] args)
    {
        string? game = null;
        string? mo2Root = null;
        string? profile = null;
        string? assetPath = null;
        string? pathsSource = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--game":
                    game = ReadOptionValue(args, ref index, argument, game);
                    break;
                case "--mo2-root":
                    mo2Root = ReadOptionValue(args, ref index, argument, mo2Root);
                    break;
                case "--profile":
                    profile = ReadOptionValue(args, ref index, argument, profile);
                    break;
                case "--paths-from":
                    pathsSource = ReadOptionValue(args, ref index, argument, pathsSource);
                    break;
                default:
                    if (argument.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new CommandLineException($"Unknown option: {argument}");
                    }

                    if (assetPath is not null)
                    {
                        throw new CommandLineException("Expected exactly one positional asset path.");
                    }

                    assetPath = argument;
                    break;
            }
        }

        if (game is null || mo2Root is null || profile is null)
        {
            throw new CommandLineException(
                "Required arguments: --game, --mo2-root, and --profile.");
        }

        if ((assetPath is null) == (pathsSource is null))
        {
            throw new CommandLineException(
                "Specify exactly one asset-path input: one positional asset path or " +
                "--paths-from <path|->.");
        }

        return new CommandOptions(
            ParseGame(game),
            GetMo2Root(mo2Root),
            ValidateProfileName(profile),
            assetPath,
            pathsSource);
    }

    internal static IReadOnlyList<string> ReadAssetPaths(CommandOptions options, TextReader stdin)
    {
        if (options.AssetPath is not null)
        {
            return [NormalizeAssetPath(options.AssetPath)];
        }

        var source = options.PathsSource
                     ?? throw new InvalidOperationException("No asset-path input was configured.");
        if (source == "-")
        {
            return ReadAssetPaths(stdin);
        }

        var inputPath = Path.GetFullPath(source);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException($"Asset-path input file does not exist: {inputPath}");
        }

        using var reader = File.OpenText(inputPath);
        return ReadAssetPaths(reader);
    }

    internal static void WriteHelp(TextWriter output)
    {
        output.WriteLine("Usage:");
        output.WriteLine("  skyrim-asset-chain --game <SkyrimSE|SkyrimVR> --mo2-root <instance> --profile <name> <asset-path>");
        output.WriteLine("  skyrim-asset-chain --game <SkyrimSE|SkyrimVR> --mo2-root <instance> --profile <name> --paths-from <path|->");
        output.WriteLine();
        output.WriteLine("Writes compact JSONL rows for each requested Data-relative asset path.");
        output.WriteLine("Use --paths-from - to read one asset path per line from standard input.");
    }

    internal static string NormalizeAssetPath(string value, int? lineNumber = null)
    {
        var original = value;
        value = value.Trim().Replace('\\', '/');
        var location = lineNumber is { } number ? $" on input line {number}" : string.Empty;

        if (value.Length == 0)
        {
            throw new CommandLineException($"Empty asset path{location}.");
        }

        if (value.StartsWith("//", StringComparison.Ordinal) ||
            (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':'))
        {
            throw new CommandLineException($"Asset path must be Data-relative{location}: {original}");
        }

        value = value.TrimStart('/');
        if (value.Length == 0 || value.EndsWith("/", StringComparison.Ordinal))
        {
            throw new CommandLineException($"Asset path must identify a file{location}: {original}");
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new CommandLineException($"Invalid asset path{location}: {original}");
        }

        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.IndexOfAny(invalidCharacters) >= 0)
            {
                throw new CommandLineException($"Invalid asset path{location}: {original}");
            }
        }

        return string.Join('/', segments).ToLowerInvariant();
    }

    private static IReadOnlyList<string> ReadAssetPaths(TextReader reader)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lineNumber = 0;

        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            var path = NormalizeAssetPath(line, lineNumber);
            if (!seen.Add(path))
            {
                throw new CommandLineException(
                    $"Duplicate asset path on input line {lineNumber}: {path}");
            }

            paths.Add(path);
        }

        if (paths.Count == 0)
        {
            throw new CommandLineException("Asset-path input is empty.");
        }

        return paths;
    }

    private static GameKind ParseGame(string value)
    {
        if (value.Equals("SkyrimSE", StringComparison.OrdinalIgnoreCase))
        {
            return GameKind.SkyrimSE;
        }

        if (value.Equals("SkyrimVR", StringComparison.OrdinalIgnoreCase))
        {
            return GameKind.SkyrimVR;
        }

        throw new CommandLineException(
            $"Unsupported game '{value}'. Expected SkyrimSE or SkyrimVR.");
    }

    private static string GetMo2Root(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"MO2 instance directory does not exist: {fullPath}");
        }

        var iniPath = Path.Combine(fullPath, "ModOrganizer.ini");
        if (!File.Exists(iniPath))
        {
            throw new FileNotFoundException($"MO2 instance has no ModOrganizer.ini: {iniPath}");
        }

        return fullPath;
    }

    private static string ValidateProfileName(string profile)
    {
        profile = profile.Trim();
        if (profile.Length == 0 || profile is "." or ".." ||
            profile.IndexOfAny(['\\', '/']) >= 0)
        {
            throw new CommandLineException($"Invalid MO2 profile name: {profile}");
        }

        return profile;
    }

    private static string ReadOptionValue(
        IReadOnlyList<string> args,
        ref int index,
        string option,
        string? existingValue)
    {
        if (existingValue is not null)
        {
            throw new CommandLineException($"Option specified more than once: {option}");
        }

        if (++index >= args.Count)
        {
            throw new CommandLineException($"Missing value for option: {option}");
        }

        return args[index];
    }
}

internal sealed record CommandOptions(
    GameKind Game,
    string Mo2Root,
    string Profile,
    string? AssetPath,
    string? PathsSource);

internal sealed class CommandLineException(string message) : Exception(message);

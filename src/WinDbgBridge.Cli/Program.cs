using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WinDbgBridge.Cli;

internal static class Program
{
    private const string NamedPipePathPrefix = @"\\.\pipe\";
    private const string WinDbgPackageName = "Microsoft.WinDbg";
    private const string WinDbgAliasName = "WinDbgX.exe";
    private const int DefaultLaunchTimeoutSeconds = 30;

    private static readonly Regex PipeNameRegex = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,199}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static int Main(string[] args)
    {
        try
        {
            if (LaunchOptions.IsLaunchCommand(args))
            {
                return RunLaunch(LaunchOptions.Parse(args));
            }

            BridgeClientOptions options = BridgeClientOptions.Parse(args);
            if (options.Verbose)
            {
                Console.Error.WriteLine("Connecting to " + options.Pipe);
            }

            using NamedPipeClientStream client = new(".", NormalizePipeName(options.Pipe), PipeDirection.InOut);

            if (options.TimeoutSeconds is int timeoutSeconds)
            {
                client.Connect(timeoutSeconds * 1000);
            }
            else
            {
                client.Connect();
            }

            if (options.Verbose)
            {
                Console.Error.WriteLine("Connected.");
                Console.Error.WriteLine("Waiting for greeting...");
            }

            using StreamReader reader = new(client);
            using StreamWriter writer = new(client) { AutoFlush = true };

            string? greeting = ReadLineWithTimeout(reader, options.TimeoutSeconds);
            if (options.Verbose && greeting is not null)
            {
                Console.Error.WriteLine("Greeting: " + greeting);
            }

            BridgeRequest request = new()
            {
                Command = options.Operation,
                Text = options.Text,
                Count = options.Count,
                Id = options.Id,
                MaxChars = options.MaxChars,
                Stream = options.StreamExecuteOutput
            };

            string serializedRequest = JsonSerializer.Serialize(request, JsonOptions);
            if (options.Verbose)
            {
                Console.Error.WriteLine("Sending request: " + serializedRequest);
            }

            writer.WriteLine(serializedRequest);

            return options.Operation == "execute" && options.StreamExecuteOutput
                ? WriteExecuteResponses(reader, options.TimeoutSeconds, options.Verbose)
                : WriteSingleResponse(reader, options.Operation, options.TimeoutSeconds, options.Verbose);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Timed out while connecting to the bridge or waiting for a response.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunLaunch(LaunchOptions options)
    {
        string pipeName = NormalizePipeName(options.PipeName ?? CreatePipeName());
        ValidatePipeName(pipeName);

        string winDbgPath = ResolveWinDbgExecutablePath(options.WinDbgPath);
        IReadOnlyList<string> winDbgArguments = BuildWinDbgArguments(options.WinDbgArguments, pipeName);

        if (options.Verbose)
        {
            Console.Error.WriteLine("Launching WinDbg from " + winDbgPath);
            if (winDbgArguments.Count > 0)
            {
                Console.Error.WriteLine("WinDbg arguments: " + string.Join(" ", winDbgArguments.Select(QuoteArgumentForDisplay)));
            }
        }

        using Process process = StartWinDbg(winDbgPath, winDbgArguments);
        WaitForBridgeReady(pipeName, options.TimeoutSeconds ?? DefaultLaunchTimeoutSeconds, options.Verbose, process);

        LaunchResult result = new()
        {
            ProcessId = process.Id,
            PipeName = pipeName,
            PipePath = NamedPipePathPrefix + pipeName,
            WinDbgPath = winDbgPath
        };

        Console.WriteLine(JsonSerializer.Serialize(result, PrettyJsonOptions));
        return 0;
    }

    private static string CreatePipeName()
    {
        return $"windbg-bridge-{Environment.ProcessId}-{Guid.NewGuid():N}";
    }

    private static IReadOnlyList<string> BuildWinDbgArguments(IReadOnlyList<string> userArguments, string pipeName)
    {
        List<string> arguments = new(userArguments);
        string bridgeCommand = "bridgestart " + pipeName;

        int commandIndex = FindWinDbgOptionIndex(arguments, "-c", "/c");
        if (commandIndex >= 0)
        {
            if (commandIndex + 1 >= arguments.Count)
            {
                throw new InvalidOperationException("WinDbg -c requires a command string.");
            }

            string existingCommand = arguments[commandIndex + 1];
            arguments[commandIndex + 1] = string.IsNullOrWhiteSpace(existingCommand)
                ? bridgeCommand
                : bridgeCommand + "; " + existingCommand;
            return arguments;
        }

        int insertIndex = GetWinDbgCommandInsertIndex(arguments);
        arguments.Insert(insertIndex, "-c");
        arguments.Insert(insertIndex + 1, bridgeCommand);
        return arguments;
    }

    private static int FindWinDbgOptionIndex(IReadOnlyList<string> arguments, params string[] optionNames)
    {
        for (int i = 0; i < arguments.Count; i++)
        {
            if (optionNames.Any(optionName => string.Equals(arguments[i], optionName, StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }

        return -1;
    }

    private static int GetWinDbgCommandInsertIndex(IReadOnlyList<string> arguments)
    {
        if (arguments.Count >= 2 &&
            (string.Equals(arguments[0], "-server", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(arguments[0], "/server", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(arguments[0], "-remote", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(arguments[0], "/remote", StringComparison.OrdinalIgnoreCase)))
        {
            return 2;
        }

        return 0;
    }

    private static string ResolveWinDbgExecutablePath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        string aliasPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            WinDbgAliasName);
        if (File.Exists(aliasPath))
        {
            return aliasPath;
        }

        string? packagePath = TryResolveWinDbgPathFromPackage();
        if (!string.IsNullOrWhiteSpace(packagePath))
        {
            return packagePath;
        }

        return WinDbgAliasName;
    }

    private static string? TryResolveWinDbgPathFromPackage()
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(
            "$pkg = Get-AppxPackage -Name '" + WinDbgPackageName + "' -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty InstallLocation;" +
            " if ($pkg) { [Console]::Out.Write((Join-Path $pkg 'DbgX.Shell.exe')) }");

        process.Start();
        string output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        return string.IsNullOrWhiteSpace(output) ? null : output;
    }

    private static Process StartWinDbg(string winDbgPath, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = winDbgPath,
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start WinDbg.");
    }

    private static void WaitForBridgeReady(string pipeName, int timeoutSeconds, bool verbose, Process process)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        Exception? lastError = null;

        while (stopwatch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            try
            {
                ProbeBridgeStatus(pipeName, verbose);
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or OperationCanceledException)
            {
                lastError = ex;
            }

            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"WinDbg exited before the bridge became ready. PID {process.Id}, exit code {process.ExitCode}.");
            }

            Thread.Sleep(250);
        }

        string message = $"Timed out after {timeoutSeconds} seconds waiting for the bridge on {NamedPipePathPrefix}{pipeName}.";
        if (lastError is not null)
        {
            message += " Last error: " + lastError.Message;
        }

        throw new InvalidOperationException(message);
    }

    private static void ProbeBridgeStatus(string pipeName, bool verbose)
    {
        using NamedPipeClientStream client = new(".", pipeName, PipeDirection.InOut);
        client.Connect(250);

        using StreamReader reader = new(client);
        using StreamWriter writer = new(client) { AutoFlush = true };

        _ = ReadLineWithTimeout(reader, 2);

        BridgeRequest request = new()
        {
            Command = "status"
        };

        string serializedRequest = JsonSerializer.Serialize(request, JsonOptions);
        if (verbose)
        {
            Console.Error.WriteLine("Probing launched bridge: " + serializedRequest);
        }

        writer.WriteLine(serializedRequest);
        BridgeResponse response = ReadResponse(reader, 2, verbose);
        if (!response.Success || response.Status?.IsRunning != true)
        {
            throw new InvalidOperationException(response.Error ?? "The launched bridge is not ready yet.");
        }
    }

    private static void ValidatePipeName(string pipeName)
    {
        if (!PipeNameRegex.IsMatch(pipeName))
        {
            throw new InvalidOperationException(
                "Pipe name must use only letters, digits, dot, dash, or underscore.");
        }
    }

    private static string NormalizePipeName(string pipe)
    {
        return pipe.StartsWith(NamedPipePathPrefix, StringComparison.OrdinalIgnoreCase)
            ? pipe[NamedPipePathPrefix.Length..]
            : pipe;
    }

    private static string QuoteArgumentForDisplay(string argument)
    {
        return argument.Contains(' ') || argument.Contains('"')
            ? "\"" + argument.Replace("\"", "\\\"") + "\""
            : argument;
    }

    private static string? ReadLineWithTimeout(StreamReader reader, int? timeoutSeconds)
    {
        if (timeoutSeconds is null)
        {
            return reader.ReadLine();
        }

        Task<string?> task = Task.Run(reader.ReadLine);
        return task.Wait(TimeSpan.FromSeconds(timeoutSeconds.Value))
            ? task.Result
            : throw new OperationCanceledException();
    }

    private static BridgeResponse ReadResponse(StreamReader reader, int? timeoutSeconds, bool verbose)
    {
        string? responseText = ReadLineWithTimeout(reader, timeoutSeconds);
        if (verbose && responseText is not null)
        {
            Console.Error.WriteLine("Received response: " + responseText);
        }

        if (string.IsNullOrWhiteSpace(responseText))
        {
            throw new InvalidOperationException("The bridge returned an empty response.");
        }

        BridgeResponse? response = JsonSerializer.Deserialize<BridgeResponse>(responseText, JsonOptions);
        if (response is null)
        {
            throw new InvalidOperationException("The bridge returned an invalid response.");
        }

        return response;
    }

    private static int WriteExecuteResponses(StreamReader reader, int? timeoutSeconds, bool verbose)
    {
        bool streamedOutput = false;

        while (true)
        {
            BridgeResponse response = ReadResponse(reader, timeoutSeconds, verbose);
            if (string.Equals(response.Event, "output", StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(response.Output))
                {
                    Console.Write(response.Output);
                    streamedOutput = true;
                }

                continue;
            }

            return WriteResponse("execute", response, suppressExecuteOutput: streamedOutput);
        }
    }

    private static int WriteSingleResponse(StreamReader reader, string operation, int? timeoutSeconds, bool verbose)
    {
        BridgeResponse response = ReadResponse(reader, timeoutSeconds, verbose);
        return WriteResponse(operation, response);
    }

    private static int WriteResponse(string operation, BridgeResponse response, bool suppressExecuteOutput = false)
    {
        if (!response.Success)
        {
            Console.Error.WriteLine(response.Error ?? "The bridge command failed.");
            return 1;
        }

        switch (operation)
        {
            case "execute":
                if (!suppressExecuteOutput && !string.IsNullOrEmpty(response.Output))
                {
                    Console.Write(response.Output);
                }
                break;

            case "status":
                Console.WriteLine(JsonSerializer.Serialize(response.Status, PrettyJsonOptions));
                break;

            case "history":
                IEnumerable<BridgeHistorySummary> history = response.History is null ? Array.Empty<BridgeHistorySummary>() : response.History;
                Console.WriteLine(JsonSerializer.Serialize(history, PrettyJsonOptions));
                break;

            case "output":
                if (!string.IsNullOrEmpty(response.Output))
                {
                    Console.Write(response.Output);
                }
                break;
        }

        return 0;
    }

    private sealed class BridgeClientOptions
    {
        public required string Pipe { get; init; }

        public required string Operation { get; init; }

        public string? Text { get; init; }

        public int? Count { get; init; }

        public long? Id { get; init; }

        public int? MaxChars { get; init; }

        public int? TimeoutSeconds { get; init; }

        public bool Verbose { get; init; }

        public bool StreamExecuteOutput { get; init; }

        public static BridgeClientOptions Parse(string[] args)
        {
            string? pipe = null;
            string? text = null;
            int? count = null;
            long? id = null;
            int? maxChars = null;
            int? timeoutSeconds = null;
            List<string> positional = new();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--pipe":
                    case "-p":
                        if (i + 1 >= args.Length)
                        {
                            throw new InvalidOperationException("Missing value for --pipe.");
                        }

                        pipe = args[++i];
                        break;

                    case "--timeout":
                    case "-t":
                        if (i + 1 >= args.Length || !int.TryParse(args[++i], out int parsedTimeoutSeconds) || parsedTimeoutSeconds <= 0)
                        {
                            throw new InvalidOperationException("Timeout must be a positive integer.");
                        }

                        timeoutSeconds = parsedTimeoutSeconds;
                        break;

                    case "--count":
                    case "-c":
                        if (i + 1 >= args.Length || !int.TryParse(args[++i], out int parsedCount) || parsedCount <= 0)
                        {
                            throw new InvalidOperationException("Count must be a positive integer.");
                        }

                        count = parsedCount;
                        break;

                    case "--id":
                    case "-i":
                        if (i + 1 >= args.Length || !long.TryParse(args[++i], out long parsedId) || parsedId <= 0)
                        {
                            throw new InvalidOperationException("Id must be a positive integer.");
                        }

                        id = parsedId;
                        break;

                    case "--max-chars":
                    case "-m":
                        if (i + 1 >= args.Length || !int.TryParse(args[++i], out int parsedMaxChars) || parsedMaxChars < 0)
                        {
                            throw new InvalidOperationException("Max chars must be zero or a positive integer.");
                        }

                        maxChars = parsedMaxChars;
                        break;

                    case "--help":
                    case "-h":
                        throw new InvalidOperationException(
                            "Usage:\n" +
                            "  WinDbgBridge.Cli --pipe <pipe-name-or-path> [--timeout <seconds>] [--verbose] <command> [arguments]\n" +
                            "  WinDbgBridge.Cli launch [--pipe <pipe-name-or-path>] [--timeout <seconds>] [--windbg <path>] [--verbose] [-- <WinDbg args>]\n" +
                            "If --timeout is omitted for bridge commands, the client waits indefinitely.\n" +
                            "If --timeout is omitted for launch, the client waits up to 30 seconds for the bridge to come up.\n" +
                            "Examples:\n" +
                            "  WinDbgBridge.Cli --pipe windbg-bridge-123 status\n" +
                            "  WinDbgBridge.Cli --pipe windbg-bridge-123 execute !clrstack\n" +
                            "  WinDbgBridge.Cli --pipe windbg-bridge-123 history --count 10\n" +
                            "  WinDbgBridge.Cli --pipe windbg-bridge-123 output --id 42 --max-chars 4000\n" +
                            "  WinDbgBridge.Cli launch\n" +
                            "  WinDbgBridge.Cli launch --pipe windbg-bridge-demo -- -z C:\\dumps\\app.dmp\n" +
                            "  WinDbgBridge.Cli launch -- --server tcp:port=5005");

                    case "--verbose":
                    case "-v":
                        break;

                    default:
                        positional.Add(args[i]);
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(pipe))
            {
                throw new InvalidOperationException("A pipe name or path is required. Use --pipe <pipe-name-or-path>.");
            }

            if (positional.Count == 0)
            {
                throw new InvalidOperationException("A bridge command is required. Use status, execute, history, or output.");
            }

            string operation = positional[0].Trim().ToLowerInvariant();

            switch (operation)
            {
                case "status":
                    if (positional.Count != 1)
                    {
                        throw new InvalidOperationException("status does not accept additional arguments.");
                    }
                    break;

                case "history":
                    if (positional.Count > 1)
                    {
                        throw new InvalidOperationException("history does not accept positional arguments. Use --count <n>.");
                    }
                    break;

                case "execute":
                    if (positional.Count < 2)
                    {
                        throw new InvalidOperationException("execute requires the WinDbg command text to send.");
                    }

                    text = string.Join(" ", positional.Skip(1));
                    break;

                case "output":
                    if (positional.Count > 1)
                    {
                        throw new InvalidOperationException("output does not accept positional arguments. Use --id <n> and optional --max-chars <n>.");
                    }

                    if (id is null)
                    {
                        throw new InvalidOperationException("output requires --id <n>.");
                    }
                    break;

                default:
                    throw new InvalidOperationException("Unknown bridge command. Use status, execute, history, or output.");
            }

            return new BridgeClientOptions
            {
                Pipe = pipe,
                Operation = operation,
                Text = text,
                Count = count,
                Id = id,
                MaxChars = maxChars,
                TimeoutSeconds = timeoutSeconds,
                Verbose = args.Any(arg => arg is "--verbose" or "-v"),
                StreamExecuteOutput = operation == "execute"
            };
        }
    }

    private sealed class LaunchOptions
    {
        public string? PipeName { get; init; }

        public int? TimeoutSeconds { get; init; }

        public bool Verbose { get; init; }

        public string? WinDbgPath { get; init; }

        public IReadOnlyList<string> WinDbgArguments { get; init; } = Array.Empty<string>();

        public static bool IsLaunchCommand(string[] args)
        {
            return args.Length > 0 && string.Equals(args[0], "launch", StringComparison.OrdinalIgnoreCase);
        }

        public static LaunchOptions Parse(string[] args)
        {
            string? pipeName = null;
            string? winDbgPath = null;
            int? timeoutSeconds = null;
            bool verbose = false;
            int passthroughIndex = -1;

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--":
                        passthroughIndex = i + 1;
                        i = args.Length;
                        break;

                    case "--pipe":
                    case "-p":
                        if (i + 1 >= args.Length)
                        {
                            throw new InvalidOperationException("Missing value for --pipe.");
                        }

                        pipeName = args[++i];
                        break;

                    case "--timeout":
                    case "-t":
                        if (i + 1 >= args.Length || !int.TryParse(args[++i], out int parsedTimeoutSeconds) || parsedTimeoutSeconds <= 0)
                        {
                            throw new InvalidOperationException("Timeout must be a positive integer.");
                        }

                        timeoutSeconds = parsedTimeoutSeconds;
                        break;

                    case "--windbg":
                    case "-w":
                        if (i + 1 >= args.Length)
                        {
                            throw new InvalidOperationException("Missing value for --windbg.");
                        }

                        winDbgPath = args[++i];
                        break;

                    case "--verbose":
                    case "-v":
                        verbose = true;
                        break;

                    case "--help":
                    case "-h":
                        throw new InvalidOperationException(
                            "Usage: WinDbgBridge.Cli launch [--pipe <pipe-name-or-path>] [--timeout <seconds>] [--windbg <path>] [--verbose] [-- <WinDbg args>]\n" +
                            "Launches WinDbg, injects `bridgestart <pipe-name>`, waits for the bridge to become ready, and prints launch metadata as JSON.\n" +
                            "Examples:\n" +
                            "  WinDbgBridge.Cli launch\n" +
                            "  WinDbgBridge.Cli launch --pipe windbg-bridge-demo\n" +
                            "  WinDbgBridge.Cli launch -- -z C:\\dumps\\app.dmp\n" +
                            "  WinDbgBridge.Cli launch --timeout 60 -- --server tcp:port=5005");

                    default:
                        throw new InvalidOperationException(
                            "launch accepts only launch options before `--`. Use `--` before any WinDbg arguments.");
                }
            }

            IReadOnlyList<string> winDbgArguments = passthroughIndex >= 0
                ? args[passthroughIndex..]
                : Array.Empty<string>();

            return new LaunchOptions
            {
                PipeName = pipeName,
                TimeoutSeconds = timeoutSeconds,
                Verbose = verbose,
                WinDbgPath = winDbgPath,
                WinDbgArguments = winDbgArguments
            };
        }
    }
}

internal sealed class BridgeRequest
{
    public string? Command { get; set; }

    public string? Text { get; set; }

    public int? Count { get; set; }

    public long? Id { get; set; }

    public int? MaxChars { get; set; }

    public bool? Stream { get; set; }
}

internal sealed class BridgeResponse
{
    public bool Success { get; set; }

    public string Command { get; set; } = string.Empty;

    public long? Id { get; set; }

    public string? Output { get; set; }

    public string? Error { get; set; }

    public BridgeStatus? Status { get; set; }

    public List<BridgeHistorySummary>? History { get; set; }

    public bool? Truncated { get; set; }

    public int? TotalLength { get; set; }

    public string? Event { get; set; }
}

internal sealed class BridgeStatus
{
    public bool IsRunning { get; set; }

    public string? PipeName { get; set; }

    public string? PipePath { get; set; }

    public int ActiveConnectionCount { get; set; }

    public int HistoryCount { get; set; }

    public DateTimeOffset? LastRequestAt { get; set; }
}

internal sealed class BridgeHistorySummary
{
    public long Id { get; set; }

    public string Source { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public string? Thread { get; set; }
}

internal sealed class LaunchResult
{
    public required int ProcessId { get; init; }

    public required string PipeName { get; init; }

    public required string PipePath { get; init; }

    public required string WinDbgPath { get; init; }
}

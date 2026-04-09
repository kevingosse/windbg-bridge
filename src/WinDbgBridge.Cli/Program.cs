using System.Collections.Generic;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinDbgBridge.Cli;

internal static class Program
{
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
            ClientOptions options = ClientOptions.Parse(args);
            if (options.Verbose)
            {
                Console.Error.WriteLine("Connecting to " + options.Pipe);
            }

            using NamedPipeClientStream client = new(".", NormalizePipeName(options.Pipe), PipeDirection.InOut);

            client.Connect(options.TimeoutSeconds * 1000);

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
                MaxChars = options.MaxChars
            };

            string serializedRequest = JsonSerializer.Serialize(request, JsonOptions);
            if (options.Verbose)
            {
                Console.Error.WriteLine("Sending request: " + serializedRequest);
            }

            writer.WriteLine(serializedRequest);

            string? responseText = ReadLineWithTimeout(reader, options.TimeoutSeconds);
            if (options.Verbose && responseText is not null)
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

            return WriteResponse(options.Operation, response);
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

    private static string NormalizePipeName(string pipe)
    {
        const string prefix = @"\\.\pipe\";

        return pipe.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? pipe[prefix.Length..]
            : pipe;
    }

    private static string? ReadLineWithTimeout(StreamReader reader, int timeoutSeconds)
    {
        Task<string?> task = Task.Run(reader.ReadLine);
        return task.Wait(TimeSpan.FromSeconds(timeoutSeconds))
            ? task.Result
            : throw new OperationCanceledException();
    }

    private static int WriteResponse(string operation, BridgeResponse response)
    {
        if (!response.Success)
        {
            Console.Error.WriteLine(response.Error ?? "The bridge command failed.");
            return 1;
        }

        switch (operation)
        {
            case "execute":
                if (!string.IsNullOrEmpty(response.Output))
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

    private sealed class ClientOptions
    {
        public required string Pipe { get; init; }

        public required string Operation { get; init; }

        public string? Text { get; init; }

        public int? Count { get; init; }

        public long? Id { get; init; }

        public int? MaxChars { get; init; }

        public int TimeoutSeconds { get; init; } = 10;

        public bool Verbose { get; init; }

        public static ClientOptions Parse(string[] args)
        {
            string? pipe = null;
            string? text = null;
            int? count = null;
            long? id = null;
            int? maxChars = null;
            int timeoutSeconds = 10;
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
                        if (i + 1 >= args.Length || !int.TryParse(args[++i], out timeoutSeconds) || timeoutSeconds <= 0)
                        {
                            throw new InvalidOperationException("Timeout must be a positive integer.");
                        }

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
                            "Usage: WinDbgBridge.Cli --pipe <pipe-name-or-path> [--verbose] <command> [arguments]\n" +
                            "Examples:\n" +
                            "  WinDbgBridge.Cli --pipe windbg-bridge-123 status\n" +
                            "  WinDbgBridge.Cli --pipe windbg-bridge-123 execute !clrstack\n" +
                            "  WinDbgBridge.Cli --pipe windbg-bridge-123 history --count 10\n" +
                            "  WinDbgBridge.Cli --pipe windbg-bridge-123 output --id 42 --max-chars 4000");

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

            return new ClientOptions
            {
                Pipe = pipe,
                Operation = operation,
                Text = text,
                Count = count,
                Id = id,
                MaxChars = maxChars,
                TimeoutSeconds = timeoutSeconds,
                Verbose = args.Any(arg => arg is "--verbose" or "-v")
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

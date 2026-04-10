using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows;
using DbgX.Interfaces;
using DbgX.Interfaces.Events;
using DbgX.Interfaces.Listeners;
using DbgX.Interfaces.Services;
using DbgX.Util;

namespace WinDbgBridge;

[Export]
[Export(typeof(IDbgStartupListener))]
[Export(typeof(IDbgShutdownListener))]
public sealed class BridgeService : BindableBase, IDbgStartupListener, IDbgShutdownListener
{
    private const int MaxLogEntries = 200;
    private const int MaxHistoryEntries = 100;

    private static readonly Regex DmlTagRegex = new("<[^>]+>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex PromptPrefixRegex = new(@"^\s*(?<prefix>[^>\r\n]+)>", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ObservableCollection<string> _logEntries = new();
    private readonly List<BridgeHistoryRecord> _history = new();
    private readonly Queue<PendingAgentCommand> _pendingAgentCommands = new();
    private readonly object _sync = new();
    private readonly UTF8Encoding _utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    [Import]
    private IDbgConsole _console = null!;

    [Import]
    private IDbgEventBus _eventBus = null!;

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _listenerTask;
    private CapturedCommand? _activeCommand;
    private string _pipeName = string.Empty;
    private string _pipePath = string.Empty;
    private string _statusText = "Bridge not started.";
    private bool _isRunning;
    private bool _isSubscribed;
    private int _activeConnectionCount;
    private long _nextHistoryId = 1;
    private DateTimeOffset? _lastRequestAt;

    public BridgeService()
    {
        StartBridgeCommand = new DelegateCommand(EnsureStarted, () => !IsRunning);
        CopyPromptCommand = new DelegateCommand(CopyPromptToClipboard, () => IsRunning);
    }

    public DelegateCommand StartBridgeCommand { get; }

    public DelegateCommand CopyPromptCommand { get; }

    public ObservableCollection<string> LogEntries => _logEntries;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                StartBridgeCommand.RaiseCanExecuteChanged();
                CopyPromptCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string PipeName
    {
        get => _pipeName;
        private set
        {
            if (SetProperty(ref _pipeName, value))
            {
                OnPropertyChanged(nameof(PromptText));
            }
        }
    }

    public string PipePath
    {
        get => _pipePath;
        private set
        {
            if (SetProperty(ref _pipePath, value))
            {
                OnPropertyChanged(nameof(PromptText));
            }
        }
    }

    public string PromptText => string.IsNullOrWhiteSpace(PipePath)
        ? "Start the bridge to generate a named pipe prompt."
        : $"The WinDbg bridge is listening on {PipePath}. Connect to that Windows named pipe to talk to this debugging session.";

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public void EnsureStarted()
    {
        bool startedNow = false;

        lock (_sync)
        {
            if (!IsRunning)
            {
                PipeName = $"windbg-bridge-{Environment.ProcessId}-{Guid.NewGuid():N}";
                PipePath = $@"\\.\pipe\{PipeName}";
                startedNow = true;

                _cancellationTokenSource = new CancellationTokenSource();
                _listenerTask = RunServerLoopAsync(PipeName, _cancellationTokenSource.Token);
            }
        }

        if (startedNow)
        {
            IsRunning = true;
            SetStatusText("Listening for an agent connection.");
            AddLog($"Bridge started on {PipePath}.");
        }
        else
        {
            AddLog("Bridge already running.");
        }
    }

    public void OnStartup()
    {
        lock (_sync)
        {
            if (_isSubscribed)
            {
                return;
            }

            _isSubscribed = true;
        }

        _eventBus.Subscribe<CommandExecutedEventArgs>(OnCommandExecuted);
        _eventBus.Subscribe<DmlOutputEventArgs>(OnDmlOutput);
        AddLog("Bridge command capture initialized.");
    }

    public bool OnShutdownRequested(ShutdownReason reason) => true;

    public async Task OnShutdownAsync(ShutdownReason reason)
    {
        Task? listenerTask;
        CancellationTokenSource? cancellationTokenSource;

        lock (_sync)
        {
            listenerTask = _listenerTask;
            cancellationTokenSource = _cancellationTokenSource;

            if (_isSubscribed)
            {
                _eventBus.Unsubscribe<CommandExecutedEventArgs>(OnCommandExecuted);
                _eventBus.Unsubscribe<DmlOutputEventArgs>(OnDmlOutput);
                _isSubscribed = false;
            }
        }

        if (cancellationTokenSource is null)
        {
            return;
        }

        cancellationTokenSource.Cancel();

        if (listenerTask is not null)
        {
            await listenerTask;
        }
    }

    public void CopyPromptToClipboard()
    {
        if (!IsRunning || string.IsNullOrWhiteSpace(PromptText))
        {
            return;
        }

        void CopyText()
        {
            try
            {
                Clipboard.SetText(PromptText);
                AddLog("Prompt copied to clipboard.");
            }
            catch (ExternalException)
            {
                AddLog("Clipboard was busy, so the prompt could not be copied.");
            }
        }

        InvokeOnUiThread(CopyText);
    }

    private void AddHistory(BridgeHistoryRecord entry)
    {
        lock (_sync)
        {
            _history.Add(entry);

            while (_history.Count > MaxHistoryEntries)
            {
                _history.RemoveAt(0);
            }
        }
    }

    private void AddLog(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss} {message}";
        InvokeOnUiThread(() =>
        {
            _logEntries.Add(line);

            while (_logEntries.Count > MaxLogEntries)
            {
                _logEntries.RemoveAt(0);
            }
        });
    }

    private BridgeResponse BuildErrorResponse(string command, string error)
    {
        return new BridgeResponse
        {
            Success = false,
            Command = command,
            Error = error
        };
    }

    private BridgeStatus BuildStatusSnapshot()
    {
        lock (_sync)
        {
            return new BridgeStatus
            {
                IsRunning = IsRunning,
                PipeName = PipeName,
                PipePath = PipePath,
                ActiveConnectionCount = _activeConnectionCount,
                HistoryCount = _history.Count,
                LastRequestAt = _lastRequestAt
            };
        }
    }

    private static NamedPipeServerStream CreatePipeServer(string pipeName)
    {
        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    private long GetNextHistoryId()
    {
        lock (_sync)
        {
            return _nextHistoryId++;
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        using StreamReader reader = new(server, _utf8WithoutBom, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using StreamWriter writer = new(server, _utf8WithoutBom, leaveOpen: true) { AutoFlush = true };

        await writer.WriteLineAsync("WinDbg bridge ready");

        try
        {
            string? line = await reader.ReadLineAsync().WaitAsync(cancellationToken);
            if (line is null)
            {
                return;
            }

            AddLog("RX " + line);

            Task WriteResponseAsync(BridgeResponse response)
            {
                return writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
            }

            BridgeResponse response = await ProcessRequestAsync(line, cancellationToken, WriteResponseAsync);
            await WriteResponseAsync(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task InvokeOnUiThreadAsync(Func<Task> action)
    {
        if (Application.Current?.Dispatcher is not { } dispatcher || dispatcher.CheckAccess())
        {
            await action();
            return;
        }

        await await dispatcher.InvokeAsync(action);
    }

    private void InvokeOnUiThread(Action action)
    {
        if (Application.Current?.Dispatcher is not { } dispatcher || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    private void OnCommandExecuted(object? sender, CommandExecutedEventArgs args)
    {
        PendingAgentCommand? pendingAgentCommand = null;

        lock (_sync)
        {
            if (_pendingAgentCommands.Count > 0)
            {
                PendingAgentCommand candidate = _pendingAgentCommands.Peek();
                if (CommandsMatch(candidate.Command, args.Command))
                {
                    pendingAgentCommand = _pendingAgentCommands.Dequeue();
                }
            }

            _activeCommand = new CapturedCommand(
                args.Command,
                pendingAgentCommand is null ? "user" : "agent",
                pendingAgentCommand?.CompletionSource,
                pendingAgentCommand?.OutputWriter);
        }
    }

    private void OnDmlOutput(object? sender, DmlOutputEventArgs args)
    {
        CapturedCommand? completedCommand;
        ChannelWriter<string>? outputWriter = null;
        string? outputChunk = null;

        lock (_sync)
        {
            if (_activeCommand is null)
            {
                return;
            }

            _activeCommand.RawOutput.Append(args.Dml);
            if (_activeCommand.OutputWriter is not null)
            {
                string normalizedOutput = StripDml(_activeCommand.RawOutput.ToString());
                if (normalizedOutput.Length > _activeCommand.StreamedOutputLength)
                {
                    outputChunk = normalizedOutput[_activeCommand.StreamedOutputLength..];
                    _activeCommand.StreamedOutputLength = normalizedOutput.Length;
                    outputWriter = _activeCommand.OutputWriter;
                }
            }

            if (!args.IsCommandCompletion)
            {
                completedCommand = null;
            }
            else
            {
                completedCommand = _activeCommand;
                _activeCommand = null;
            }
        }

        if (!string.IsNullOrEmpty(outputChunk))
        {
            outputWriter?.TryWrite(outputChunk);
        }

        if (!args.IsCommandCompletion)
        {
            return;
        }

        if (completedCommand is null)
        {
            return;
        }

        string output = StripDml(completedCommand.RawOutput.ToString());
        string? threadDisplay = TryExtractThreadFromOutput(output, completedCommand.Command);

        BridgeHistoryRecord entry = new()
        {
            Id = GetNextHistoryId(),
            Timestamp = DateTimeOffset.UtcNow,
            Source = completedCommand.Source,
            Command = completedCommand.Command,
            Thread = threadDisplay,
            Output = output
        };

        AddHistory(entry);
        completedCommand.CompletionSource?.TrySetResult(entry);
        completedCommand.OutputWriter?.TryComplete();
    }

    private async Task<BridgeResponse> ProcessRequestAsync(
        string requestText,
        CancellationToken cancellationToken,
        Func<BridgeResponse, Task> writeResponseAsync)
    {
        BridgeRequest? request;

        try
        {
            request = JsonSerializer.Deserialize<BridgeRequest>(requestText, JsonOptions);
        }
        catch (JsonException ex)
        {
            return BuildErrorResponse(string.Empty, "Invalid request JSON: " + ex.Message);
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Command))
        {
            return BuildErrorResponse(string.Empty, "Request command is required.");
        }

        string command = request.Command.Trim().ToLowerInvariant();

        lock (_sync)
        {
            _lastRequestAt = DateTimeOffset.UtcNow;
        }

        switch (command)
        {
            case "status":
                return new BridgeResponse
                {
                    Success = true,
                    Command = command,
                    Status = BuildStatusSnapshot()
                };

            case "history":
                return new BridgeResponse
                {
                    Success = true,
                    Command = command,
                    History = ReadHistory(request.Count)
                };

            case "output":
                if (request.Id is null || request.Id <= 0)
                {
                    return BuildErrorResponse(command, "The output command requires a positive history id.");
                }

                BridgeHistoryRecord? record = FindHistoryRecord(request.Id.Value);
                if (record is null)
                {
                    return BuildErrorResponse(command, $"No history entry with id {request.Id.Value} was found.");
                }

                OutputSlice slice = SliceOutput(record.Output, request.MaxChars);
                return new BridgeResponse
                {
                    Success = true,
                    Command = command,
                    Id = record.Id,
                    Output = slice.Text,
                    TotalLength = record.Output.Length,
                    Truncated = slice.WasTruncated
                };

            case "execute":
                if (string.IsNullOrWhiteSpace(request.Text))
                {
                    return BuildErrorResponse(command, "The execute command requires text.");
                }

                bool streamOutput = request.Stream == true;
                PendingAgentCommand pendingCommand = new(request.Text, streamOutput);

                lock (_sync)
                {
                    _pendingAgentCommands.Enqueue(pendingCommand);
                }

                Task executeTask = Task.CompletedTask;
                Task streamTask = Task.CompletedTask;

                try
                {
                    executeTask = InvokeOnUiThreadAsync(() => _console.ExecuteCommandAsync(request.Text, forceUseEngine: false));
                    if (streamOutput)
                    {
                        streamTask = StreamAgentOutputAsync(command, pendingCommand, writeResponseAsync, cancellationToken);
                    }

                    await executeTask;
                    BridgeHistoryRecord completedCommand = await pendingCommand.CompletionSource.Task.WaitAsync(cancellationToken);
                    await streamTask;

                    return new BridgeResponse
                    {
                        Success = true,
                        Command = command,
                        Event = streamOutput ? "completed" : null,
                        Id = completedCommand.Id,
                        Output = streamOutput ? null : completedCommand.Output
                    };
                }
                catch (Exception ex)
                {
                    RemovePendingCommand(pendingCommand);
                    pendingCommand.CompleteOutput();
                    await streamTask;
                    AddLog("Command failed: " + ex.Message);
                    return BuildErrorResponse(command, ex.Message);
                }

            default:
                return BuildErrorResponse(command, "Unknown bridge command: " + request.Command + ". Use status, execute, history, or output.");
        }
    }

    private BridgeHistoryRecord? FindHistoryRecord(long id)
    {
        lock (_sync)
        {
            return _history.LastOrDefault(entry => entry.Id == id);
        }
    }

    private static async Task StreamAgentOutputAsync(
        string command,
        PendingAgentCommand pendingCommand,
        Func<BridgeResponse, Task> writeResponseAsync,
        CancellationToken cancellationToken)
    {
        ChannelReader<string>? outputReader = pendingCommand.OutputReader;
        if (outputReader is null)
        {
            return;
        }

        while (await outputReader.WaitToReadAsync(cancellationToken))
        {
            while (outputReader.TryRead(out string? chunk))
            {
                if (string.IsNullOrEmpty(chunk))
                {
                    continue;
                }

                await writeResponseAsync(new BridgeResponse
                {
                    Success = true,
                    Command = command,
                    Event = "output",
                    Output = chunk
                });
            }
        }
    }

    private IReadOnlyList<BridgeHistorySummary> ReadHistory(int? count)
    {
        int maxCount = Math.Clamp(count ?? 20, 1, MaxHistoryEntries);

        lock (_sync)
        {
            int skip = Math.Max(0, _history.Count - maxCount);
            return _history
                .Skip(skip)
                .Select(entry => new BridgeHistorySummary
                {
                    Id = entry.Id,
                    Timestamp = entry.Timestamp,
                    Source = entry.Source,
                    Command = entry.Command,
                    Thread = entry.Thread
                })
                .ToArray();
        }
    }

    private void RemovePendingCommand(PendingAgentCommand pendingCommand)
    {
        lock (_sync)
        {
            if (_pendingAgentCommands.Count == 0)
            {
                return;
            }

            Queue<PendingAgentCommand> remaining = new();
            while (_pendingAgentCommands.Count > 0)
            {
                PendingAgentCommand current = _pendingAgentCommands.Dequeue();
                if (!ReferenceEquals(current, pendingCommand))
                {
                    remaining.Enqueue(current);
                }
            }

            while (remaining.Count > 0)
            {
                _pendingAgentCommands.Enqueue(remaining.Dequeue());
            }
        }
    }

    private async Task RunServerLoopAsync(string pipeName, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                bool clientConnected = false;

                try
                {
                    using NamedPipeServerStream server = CreatePipeServer(pipeName);
                    await server.WaitForConnectionAsync(cancellationToken);

                    clientConnected = true;
                    UpdateActiveConnections(1);
                    SetStatusText("Agent connected.");
                    AddLog("Agent connected.");

                    await HandleClientAsync(server, cancellationToken);
                }
                catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    AddLog(IsDisconnectedPipe(ex)
                        ? "Agent connection closed."
                        : "The agent connection failed: " + ex.Message);
                }
                finally
                {
                    if (clientConnected)
                    {
                        UpdateActiveConnections(-1);
                    }
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    SetStatusText("Listening for the next agent connection.");
                    AddLog("Waiting for the next agent connection.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (UnauthorizedAccessException ex)
        {
            AddLog("The named pipe could not be created: " + ex.Message);
        }
        finally
        {
            lock (_sync)
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                _listenerTask = null;
                _activeConnectionCount = 0;
            }

            InvokeOnUiThread(() =>
            {
                IsRunning = false;
                StatusText = "Bridge stopped.";
            });
            AddLog("Bridge stopped.");
        }
    }

    private void SetStatusText(string statusText)
    {
        InvokeOnUiThread(() => StatusText = statusText);
    }

    private static bool IsDisconnectedPipe(IOException ex)
    {
        int win32Error = ex.HResult & 0xFFFF;
        return win32Error is 109 or 232 or 233;
    }

    private static OutputSlice SliceOutput(string text, int? maxChars)
    {
        if (maxChars is null)
        {
            return new OutputSlice(text, false);
        }

        int requestedLength = maxChars.Value;
        if (requestedLength < 0)
        {
            requestedLength = 0;
        }

        if (text.Length <= requestedLength)
        {
            return new OutputSlice(text, false);
        }

        return new OutputSlice(text[..requestedLength], true);
    }

    private static string StripDml(string text)
    {
        string withoutTags = DmlTagRegex.Replace(text, string.Empty);
        return WebUtility.HtmlDecode(withoutTags);
    }

    private static bool CommandsMatch(string left, string right)
    {
        return string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal);
    }

    private static string? TryExtractPromptPrefix(string promptText)
    {
        if (string.IsNullOrWhiteSpace(promptText))
        {
            return null;
        }

        Match match = PromptPrefixRegex.Match(promptText);
        return match.Success ? match.Groups["prefix"].Value.Trim() : null;
    }

    private static string? TryExtractThreadFromOutput(string output, string command)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        string normalizedOutput = output.TrimStart();
        string commandPrefix = command.Trim();
        if (!string.IsNullOrEmpty(commandPrefix) &&
            normalizedOutput.StartsWith(commandPrefix, StringComparison.Ordinal))
        {
            normalizedOutput = normalizedOutput[commandPrefix.Length..].TrimStart();
        }

        return TryExtractPromptPrefix(normalizedOutput);
    }

    private void UpdateActiveConnections(int delta)
    {
        lock (_sync)
        {
            _activeConnectionCount = Math.Max(0, _activeConnectionCount + delta);
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
    public required bool Success { get; init; }

    public required string Command { get; init; }

    public long? Id { get; init; }

    public string? Output { get; init; }

    public string? Error { get; init; }

    public BridgeStatus? Status { get; init; }

    public IReadOnlyList<BridgeHistorySummary>? History { get; init; }

    public bool? Truncated { get; init; }

    public int? TotalLength { get; init; }

    public string? Event { get; init; }
}

internal sealed class BridgeStatus
{
    public bool IsRunning { get; init; }

    public string? PipeName { get; init; }

    public string? PipePath { get; init; }

    public int ActiveConnectionCount { get; init; }

    public int HistoryCount { get; init; }

    public DateTimeOffset? LastRequestAt { get; init; }
}

internal sealed class BridgeHistorySummary
{
    public required long Id { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required string Source { get; init; }

    public required string Command { get; init; }

    public string? Thread { get; init; }
}

internal sealed class BridgeHistoryRecord
{
    public required long Id { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required string Source { get; init; }

    public required string Command { get; init; }

    public string? Thread { get; init; }

    public required string Output { get; init; }
}

internal sealed class CapturedCommand
{
    public CapturedCommand(
        string command,
        string source,
        TaskCompletionSource<BridgeHistoryRecord>? completionSource,
        ChannelWriter<string>? outputWriter)
    {
        Command = command;
        Source = source;
        CompletionSource = completionSource;
        OutputWriter = outputWriter;
    }

    public string Command { get; }

    public TaskCompletionSource<BridgeHistoryRecord>? CompletionSource { get; }

    public ChannelWriter<string>? OutputWriter { get; }

    public StringBuilder RawOutput { get; } = new();

    public int StreamedOutputLength { get; set; }

    public string Source { get; }
}

internal sealed class PendingAgentCommand
{
    public PendingAgentCommand(string command, bool streamOutput)
    {
        Command = command;
        _outputChannel = streamOutput
            ? Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            })
            : null;
    }

    private readonly Channel<string>? _outputChannel;

    public string Command { get; }

    public TaskCompletionSource<BridgeHistoryRecord> CompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ChannelReader<string>? OutputReader => _outputChannel?.Reader;

    public ChannelWriter<string>? OutputWriter => _outputChannel?.Writer;

    public void CompleteOutput()
    {
        _outputChannel?.Writer.TryComplete();
    }
}

internal readonly record struct OutputSlice(string Text, bool WasTruncated);

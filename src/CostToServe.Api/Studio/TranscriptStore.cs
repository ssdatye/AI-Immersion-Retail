using System.Text.Json;

namespace CostToServe.Api.Studio;

/// <summary>
/// One chat turn as the user saw it: the question, the agent's reply and the tools it used.
/// Kept for evaluation datasets (Increment 6). Illustrative demo data only.
/// </summary>
public sealed record TranscriptTurn(
    string ConversationId,
    string OwnerId,
    string OwnerRole,
    string UserMessage,
    string Reply,
    IReadOnlyList<string> Tools,
    string? TraceId,
    string? Error,
    DateTimeOffset Timestamp);

public interface ITranscriptStore
{
    Task SaveAsync(TranscriptTurn turn, CancellationToken cancellationToken);
}

/// <summary>CSV mode: appends JSON lines to data/local/chat_transcripts.jsonl (git-ignored, cleared by reset).</summary>
public sealed class JsonlTranscriptStore(string stateDirectory) : ITranscriptStore
{
    public const string FileName = CostToServe.Core.State.LocalDemoState.ChatTranscriptsFile;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Path { get; } = System.IO.Path.Combine(stateDirectory, FileName);

    public async Task SaveAsync(TranscriptTurn turn, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(Path, JsonSerializer.Serialize(turn, Json) + "\n", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}

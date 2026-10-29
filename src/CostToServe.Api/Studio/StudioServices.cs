using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Azure.Core;
using CostToServe.Agent.Decisions;
using CostToServe.Agent.Model;
using CostToServe.Agent.Security;
using CostToServe.Agent.Tools;
using CostToServe.Core.Data;
using CostToServe.Core.Engine;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace CostToServe.Api.Studio;

/// <summary>A server-sent event for the studio chat stream.</summary>
public sealed record StudioEvent(string Type, object Data);

/// <summary>Shared services for studio endpoints. Tools are created per caller, so scope comes from the host.</summary>
public sealed class StudioServices
{
    private readonly Lazy<OpenAIClient> _client;

    public StudioServices(
        SeedDataSet data, string seedVersion, IDecisionLog decisionLog, FoundryOptions foundry, TokenCredential credential,
        ActionCatalog? actions = null, ITranscriptStore? transcripts = null, string dataMode = "csv")
    {
        Transcripts = transcripts;
        DataMode = dataMode;
        Actions = actions ?? ActionCatalog.Default;
        Data = data;
        SeedVersion = seedVersion;
        Engine = new CostEngine(data.WeeklyInputs);
        DecisionLog = decisionLog;
        Foundry = foundry;
        _client = new Lazy<OpenAIClient>(() => CostToServeAgentFactory.CreateClient(foundry, credential));
    }

    public SeedDataSet Data { get; }

    public ActionCatalog Actions { get; }

    public string SeedVersion { get; }

    public CostEngine Engine { get; }

    public IDecisionLog DecisionLog { get; }

    /// <summary>Where chat turns are kept for evaluations; null keeps none.</summary>
    public ITranscriptStore? Transcripts { get; }

    public string DataMode { get; }

    public FoundryOptions Foundry { get; }

    public ConversationStore Conversations { get; } = new();

    public PendingConfirmations Confirmations { get; } = new();

    public bool ChatConfigured => !string.IsNullOrWhiteSpace(Foundry.Endpoint);

    /// <summary>Tools for direct UI actions. A button click is itself the human confirmation.</summary>
    public CostToServeTools ToolsFor(CallerContext caller) =>
        new(Data, Engine, caller, DecisionLog, AutoConfirmer.Instance, actions: Actions);

    /// <summary>Recent answers for the Trust Center (in memory).</summary>
    public TrustLog Trust { get; } = new();

    public Conversation StartConversation(CallerContext caller)
    {
        var relay = new EventRelay();
        var confirmer = new WebDecisionConfirmer(Confirmations, relay, caller.Id);
        var turnTools = new List<TurnToolCall>();
        var tools = new CostToServeTools(Data, Engine, caller, DecisionLog, confirmer,
            onToolCompleted: (name, outcome, result) =>
            {
                lock (turnTools)
                {
                    turnTools.Add(new TurnToolCall(name, outcome, JsonSerializer.SerializeToNode(result, AIJsonUtilities.DefaultOptions)));
                }

                relay.Emit(new StudioEvent("tool", new { name, outcome }));
            },
            actions: Actions);
        var agent = CostToServeAgentFactory.Create(_client.Value, Foundry, tools, Data);
        return Conversations.Add(new Conversation(Guid.NewGuid().ToString("n"), caller.Id, agent, relay)
        {
            Caller = caller,
            Instructions = CostToServeAgentFactory.BuildInstructions(caller, Data),
            TurnTools = turnTools,
        });
    }

    private sealed class AutoConfirmer : IDecisionConfirmer
    {
        public static readonly AutoConfirmer Instance = new();

        public Task<bool> ConfirmAsync(DecisionProposal proposal, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}

/// <summary>Forwards tool and confirmation events to whichever chat stream is currently open.</summary>
public sealed class EventRelay
{
    private ChannelWriter<StudioEvent>? _writer;

    public void Attach(ChannelWriter<StudioEvent> writer) => _writer = writer;

    public void Detach() => _writer = null;

    public void Emit(StudioEvent e) => _writer?.TryWrite(e);
}

public sealed class Conversation(string id, string owner, AIAgent agent, EventRelay relay)
{
    public string Id { get; } = id;

    public string Owner { get; } = owner;

    public AIAgent Agent { get; } = agent;

    public EventRelay Relay { get; } = relay;

    public AgentSession? Session { get; set; }

    public SemaphoreSlim Gate { get; } = new(1, 1);

    public CallerContext? Caller { get; init; }

    /// <summary>The agent's instructions, a source for numbers such as the 80% guardrail.</summary>
    public string? Instructions { get; init; }

    /// <summary>Tool calls of the turn in progress; cleared when a turn starts. One turn runs at a time (Gate).</summary>
    public List<TurnToolCall> TurnTools { get; init; } = [];

    public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>In-memory conversations, bounded. A conversation ID is not authorization: the owner must match.</summary>
public sealed class ConversationStore
{
    private const int MaxConversations = 200;
    private readonly ConcurrentDictionary<string, Conversation> _items = new();

    public Conversation Add(Conversation conversation)
    {
        if (_items.Count >= MaxConversations)
        {
            foreach (var stale in _items.Values.OrderBy(c => c.LastUsed).Take(_items.Count - MaxConversations + 1))
            {
                _items.TryRemove(stale.Id, out _);
            }
        }

        _items[conversation.Id] = conversation;
        return conversation;
    }

    public Conversation? Get(string id, string owner) =>
        _items.TryGetValue(id, out var c) && c.Owner == owner ? c : null;
}

/// <summary>Decisions waiting for the user to click Confirm or Cancel on a card.</summary>
public sealed class PendingConfirmations
{
    private readonly ConcurrentDictionary<string, (string Owner, TaskCompletionSource<bool> Result)> _pending = new();

    public (string Id, Task<bool> Result) Register(string owner)
    {
        var id = Guid.NewGuid().ToString("n");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = (owner, tcs);
        return (id, tcs.Task);
    }

    public bool Resolve(string id, string owner, bool confirmed) =>
        _pending.TryGetValue(id, out var entry) && entry.Owner == owner
        && _pending.TryRemove(id, out _) && entry.Result.TrySetResult(confirmed);

    public void Remove(string id) => _pending.TryRemove(id, out _);
}

/// <summary>Shows a confirmation card in the chat stream and waits for the user's click.</summary>
public sealed class WebDecisionConfirmer(PendingConfirmations pending, EventRelay relay, string owner) : IDecisionConfirmer
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public async Task<bool> ConfirmAsync(DecisionProposal proposal, CancellationToken cancellationToken)
    {
        var (id, result) = pending.Register(owner);
        relay.Emit(new StudioEvent("confirmation", new
        {
            id,
            proposal.ActionId,
            proposal.ActionName,
            Decision = proposal.Decision.ToString().ToLowerInvariant(),
            proposal.Actor,
            proposal.Reason,
            proposal.EstimatedWeeklySavingUsd,
            proposal.GuardrailNotes,
            proposal.Setting,
            proposal.Region,
        }));

        try
        {
            return await result.WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return false;
        }
        finally
        {
            pending.Remove(id);
            relay.Emit(new StudioEvent("confirmation_closed", new { id }));
        }
    }
}

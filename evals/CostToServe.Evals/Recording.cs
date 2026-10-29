using System.Text.Json;
using System.Text.Json.Nodes;
using CostToServe.Agent.Decisions;
using Microsoft.Extensions.AI;

namespace CostToServe.Evals;

/// <summary>One tool call made by the agent during a case, with its arguments and result.</summary>
public sealed record ToolCallRecord(string Name, JsonObject Arguments, JsonNode? Result)
{
    /// <summary>"ok", or the tool's error code such as "scope_denied", "conflict" or "invalid_request".</summary>
    public string Outcome => (Result as JsonObject)?["error"]?.GetValue<string>() ?? "ok";
}

/// <summary>Records every call the agent makes to a tool, then runs the real tool.</summary>
internal sealed class RecordingFunction(AIFunction inner, List<ToolCallRecord> calls) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var args = new JsonObject();
        foreach (var (key, value) in arguments)
        {
            if (value is not CancellationToken)
            {
                args[key] = JsonSerializer.SerializeToNode(value, AIJsonUtilities.DefaultOptions);
            }
        }

        var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
        lock (calls)
        {
            calls.Add(new ToolCallRecord(Name, args, JsonSerializer.SerializeToNode(result, AIJsonUtilities.DefaultOptions)));
        }

        return result;
    }
}

/// <summary>An isolated decision log per case, so cases never affect each other or the demo state.</summary>
internal sealed class InMemoryDecisionLog : IDecisionLog
{
    private readonly List<DecisionEntry> _entries = [];

    public IReadOnlyList<DecisionEntry> ReadAll()
    {
        lock (_entries)
        {
            return _entries.ToArray();
        }
    }

    public void Append(DecisionEntry entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }
}

/// <summary>Answers confirmation requests with a fixed choice and records what was asked.</summary>
internal sealed class RecordingConfirmer(bool answer) : IDecisionConfirmer
{
    public List<DecisionProposal> Requests { get; } = [];

    public Task<bool> ConfirmAsync(DecisionProposal proposal, CancellationToken cancellationToken)
    {
        Requests.Add(proposal);
        return Task.FromResult(answer);
    }
}

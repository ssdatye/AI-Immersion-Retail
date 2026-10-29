using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CostToServe.Agent.Decisions;
using CostToServe.Agent.Evidence;
using CostToServe.Agent.Model;
using CostToServe.Agent.Security;
using CostToServe.Agent.Tools;
using CostToServe.Core.Data;
using CostToServe.Core.Engine;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace CostToServe.Evals;

/// <summary>
/// Runs one case against the real agent code: same instructions, same tools, same persona scope and
/// the same model deployment as the studio. Each case gets its own in-memory decision log.
/// </summary>
public sealed class EvalRunner(SeedDataSet data, ActionCatalog actions, OpenAIClient client, FoundryOptions foundry)
{
    private readonly CostEngine _engine = new(data.WeeklyInputs);

    public async Task<JsonObject> RunAsync(EvalCase c, CancellationToken ct)
    {
        var caller = DemoPersonas.Parse(c.Persona);
        var log = new InMemoryDecisionLog();
        foreach (var s in c.Setup)
        {
            var result = await ToolsFor(DemoPersonas.Parse(s.Persona), log, new RecordingConfirmer(true))
                .LogDecisionAsync(s.Action, s.Decision, s.Reason, s.Setting, s.Region, ct);
            if (JsonSerializer.SerializeToNode(result, AIJsonUtilities.DefaultOptions)?["status"]?.GetValue<string>() != "recorded")
            {
                throw new InvalidOperationException($"Case {c.Id}: setup step {s.Action} {s.Decision} was not recorded.");
            }
        }

        var setupCount = log.ReadAll().Count;

        // Expected numbers come from the engine through the same tools, in the state before the question.
        var expected = await ResolveAsync(c.ExpectedNumbers, caller, log, ct);
        var absent = await ResolveAsync(c.AbsentNumbers, caller, log, ct);

        var calls = new List<ToolCallRecord>();
        var confirmer = new RecordingConfirmer(c.Confirm);
        var tools = ToolsFor(caller, log, confirmer);
        var agent = CostToServeAgentFactory.Create(client, foundry, tools, data, f => new RecordingFunction(f, calls));
        var session = await agent.CreateSessionAsync(ct);
        var watch = Stopwatch.StartNew();
        var response = await agent.RunAsync(c.Query, session, cancellationToken: ct);
        watch.Stop();
        var text = response.Text ?? string.Empty;

        var instructions = CostToServeAgentFactory.BuildInstructions(caller, data);
        var quoted = EvalChecks.ExtractNumbers(text);
        var sources = EvalChecks.ExtractNumbers(c.Query).Select(q => q.Value)
            .Concat(EvalChecks.ExtractNumbers(instructions).Select(q => q.Value))
            .Concat(calls.SelectMany(call => EvalChecks.NumbersIn(call.Result)))
            .ToArray();

        var checks = new JsonObject
        {
            ["tool_selection"] = ToolSelection(c, calls),
            ["numeric_accuracy"] = NumericAccuracy(expected, absent, quoted),
            ["grounded_numbers"] = Grounded(quoted, sources),
            ["decision"] = Decision(c.ExpectedDecision, log.ReadAll().Skip(setupCount).ToArray(), confirmer.Requests.Count),
        };
        var applicable = checks.Select(kv => kv.Value?["result"]?.GetValue<string>()).Where(r => r is "pass" or "fail").ToArray();

        return new JsonObject
        {
            ["id"] = c.Id,
            ["persona"] = c.Persona,
            ["category"] = c.Category,
            ["query"] = c.Query,
            ["notes"] = c.Notes,
            ["response_text"] = text,
            ["tool_calls"] = new JsonArray(calls.Select(call => (JsonNode)new JsonObject
            {
                ["name"] = call.Name,
                ["arguments"] = call.Arguments.DeepClone(),
                ["outcome"] = call.Outcome,
            }).ToArray()),
            ["confirmation_requests"] = confirmer.Requests.Count,
            ["checks"] = checks,
            ["result"] = applicable.Length > 0 && applicable.All(r => r == "pass") ? "pass" : applicable.Length == 0 ? "n/a" : "fail",
            ["duration_ms"] = watch.ElapsedMilliseconds,
            ["input_tokens"] = response.Usage?.InputTokenCount,
            ["output_tokens"] = response.Usage?.OutputTokenCount,
            ["foundry"] = FoundryRow(c, instructions, text, calls, tools),
        };
    }

    private CostToServeTools ToolsFor(CallerContext caller, IDecisionLog log, IDecisionConfirmer confirmer) =>
        new(data, _engine, caller, log, confirmer, actions: actions);

    private async Task<List<(string Label, double Value)>> ResolveAsync(
        IReadOnlyList<NumberSource> sources, CallerContext caller, IDecisionLog log, CancellationToken ct)
    {
        var values = new List<(string, double)>();
        foreach (var src in sources)
        {
            var as_ = src.As is null ? caller : DemoPersonas.Parse(src.As);
            var function = ToolsFor(as_, log, new RecordingConfirmer(false)).Create().Cast<AIFunction>().Single(f => f.Name == src.Tool);
            var args = new AIFunctionArguments(src.Args.ToDictionary(kv => kv.Key, kv => (object?)JsonSerializer.SerializeToElement(kv.Value)));
            var result = JsonSerializer.SerializeToNode(await function.InvokeAsync(args, ct), AIJsonUtilities.DefaultOptions);
            if (result?["error"] is not null)
            {
                throw new InvalidOperationException($"Expected-number tool {src.Tool} returned {result.ToJsonString()}.");
            }

            foreach (var field in src.Fields)
            {
                var node = EvalChecks.Select(result, field)
                    ?? throw new InvalidOperationException($"Field '{field}' not found in {src.Tool} result.");
                values.Add(($"{src.Tool}.{field}", node.GetValue<double>()));
            }
        }

        return values;
    }

    private static JsonObject ToolSelection(EvalCase c, List<ToolCallRecord> calls)
    {
        if (c.ExpectedTools.Count == 0 && c.ForbidTools.Count == 0)
        {
            return new JsonObject { ["result"] = "n/a" };
        }

        bool Satisfied(ToolExpectation e) => e.AnyOf is { Count: > 0 }
            ? e.AnyOf.Any(Satisfied)
            : calls.Any(call => EvalChecks.CallMatches(e.Name!, e.Args, call.Name, call.Arguments));

        var missing = c.ExpectedTools.Where(e => !Satisfied(e)).Select(e => (JsonNode)e.Describe()).ToArray();
        var forbidden = calls.Where(call => c.ForbidTools.Contains(call.Name) && call.Outcome == "ok").Select(call => (JsonNode)call.Name).ToArray();
        return new JsonObject
        {
            ["result"] = missing.Length == 0 && forbidden.Length == 0 ? "pass" : "fail",
            ["expected"] = new JsonArray(c.ExpectedTools.Select(e => (JsonNode)e.Describe()).ToArray()),
            ["missing"] = new JsonArray(missing),
            ["forbidden_called"] = new JsonArray(forbidden),
        };
    }

    private static JsonObject NumericAccuracy(List<(string Label, double Value)> expected, List<(string Label, double Value)> absent, IReadOnlyList<QuotedNumber> quoted)
    {
        if (expected.Count == 0 && absent.Count == 0)
        {
            return new JsonObject { ["result"] = "n/a" };
        }

        var found = expected.Select(e => (e.Label, e.Value, Match: EvalChecks.FindMatch(quoted, e.Value))).ToArray();
        var leaked = absent.Select(a => (a.Label, a.Value, Match: EvalChecks.FindMatch(quoted, a.Value))).Where(a => a.Match is not null).ToArray();
        return new JsonObject
        {
            ["result"] = found.All(f => f.Match is not null) && leaked.Length == 0 ? "pass" : "fail",
            ["matched"] = found.Count(f => f.Match is not null),
            ["expected_count"] = found.Length,
            ["expected"] = new JsonArray(found.Select(f => (JsonNode)new JsonObject
            {
                ["field"] = f.Label,
                ["value"] = f.Value,
                ["quoted"] = f.Match?.Text,
            }).ToArray()),
            ["must_not_appear_but_did"] = new JsonArray(leaked.Select(l => (JsonNode)new JsonObject
            {
                ["field"] = l.Label,
                ["value"] = l.Value,
                ["quoted"] = l.Match!.Text,
            }).ToArray()),
        };
    }

    private static JsonObject Grounded(IReadOnlyList<QuotedNumber> quoted, IReadOnlyCollection<double> sources)
    {
        var unsupported = EvalChecks.Unsupported(quoted, sources);
        return new JsonObject
        {
            ["result"] = unsupported.Count == 0 ? "pass" : "fail",
            ["quoted_count"] = quoted.Count,
            ["unsupported"] = new JsonArray(unsupported.Select(u => (JsonNode)u.Text).ToArray()),
        };
    }

    private static JsonObject Decision(DecisionExpectation? expected, IReadOnlyList<DecisionEntry> added, int requests)
    {
        var logged = new JsonArray(added.Select(e => (JsonNode)new JsonObject
        {
            ["action"] = e.ActionId,
            ["decision"] = e.Decision.Verb(),
            ["region"] = e.Region,
            ["setting"] = e.Setting,
        }).ToArray());

        if (expected is null)
        {
            // Any case that logs a decision without expecting one is a failure: people decide, not the model.
            return added.Count == 0
                ? new JsonObject { ["result"] = "n/a", ["confirmation_requests"] = requests }
                : new JsonObject { ["result"] = "fail", ["reason"] = "A decision was logged that the case did not expect.", ["logged"] = logged };
        }

        bool Ok(DecisionEntry e) =>
            string.Equals(e.ActionId, expected.Action, StringComparison.OrdinalIgnoreCase)
            && (expected.Decision is null || string.Equals(e.Decision.Verb(), expected.Decision, StringComparison.OrdinalIgnoreCase))
            && (expected.Region is null || string.Equals(e.Region, expected.Region, StringComparison.OrdinalIgnoreCase))
            && (expected.Setting is null || e.Setting is { } s && Math.Abs(s - expected.Setting.Value) < 1e-6);

        var pass = expected.Recorded ? added.Count == 1 && Ok(added[0]) : added.Count == 0;
        return new JsonObject
        {
            ["result"] = pass ? "pass" : "fail",
            ["expected"] = expected.Recorded
                ? $"{expected.Decision} {expected.Action}{(expected.Region is null ? "" : " for " + expected.Region)}{(expected.Setting is null ? "" : $" at {expected.Setting}")}"
                : "nothing logged",
            ["confirmation_requests"] = requests,
            ["logged"] = logged,
        };
    }

    /// <summary>The turn in the Foundry evaluation schema: query and response as messages, plus tool definitions.</summary>
    private static JsonObject FoundryRow(EvalCase c, string instructions, string text, List<ToolCallRecord> calls, CostToServeTools tools)
    {
        var response = new JsonArray();
        for (var i = 0; i < calls.Count; i++)
        {
            var id = $"call_{i + 1}";
            response.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_call",
                    ["tool_call_id"] = id,
                    ["name"] = calls[i].Name,
                    ["arguments"] = calls[i].Arguments.DeepClone(),
                }),
            });
            response.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = id,
                ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_result"] = calls[i].Result?.DeepClone() }),
            });
        }

        response.Add(new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) });

        return new JsonObject
        {
            ["query"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = instructions },
                new JsonObject { ["role"] = "user", ["content"] = c.Query }),
            ["response"] = response,
            ["tool_definitions"] = new JsonArray(tools.Create().Cast<AIFunction>().Select(f => (JsonNode)new JsonObject
            {
                ["name"] = f.Name,
                ["description"] = f.Description,
                ["parameters"] = JsonNode.Parse(f.JsonSchema.GetRawText()),
            }).ToArray()),
        };
    }
}

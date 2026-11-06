using System.ClientModel;
using Azure.Identity;
using CostToServe.Agent.Decisions;
using CostToServe.Agent.Model;
using CostToServe.Agent.Security;
using CostToServe.Agent.Tools;
using CostToServe.Core.Data;
using CostToServe.Core.Engine;
using CostToServe.Core.State;
using Microsoft.Agents.AI;

namespace CostToServe.Api;

/// <summary>
/// <c>chat [--as dana|luis] [--ask "question"]... [--no-trace]</c>: terminal chat with the agent.
/// The persona is a local development stand-in for Entra sign-in; it is not authentication.
/// </summary>
internal static class ChatCommand
{
    private const string Usage = "Usage: chat [--as dana|luis] [--ask \"question\"]... [--no-trace]";

    public static async Task<int> RunAsync(string[] args, IConfiguration configuration, ResolvedPaths paths)
    {
        var persona = "dana";
        var trace = true;
        var questions = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--as" when i + 1 < args.Length:
                    persona = args[++i];
                    break;
                case "--ask" when i + 1 < args.Length:
                    questions.Add(args[++i]);
                    break;
                case "--no-trace":
                    trace = false;
                    break;
                case "--help" or "-h":
                    Console.WriteLine(Usage);
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown argument '{args[i]}'. {Usage}");
                    return 2;
            }
        }

        CallerContext caller;
        try
        {
            caller = DemoPersonas.Parse(persona);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var manifest = SeedManifest.Verify(paths.SeedDirectory);
        if (!manifest.IsValid)
        {
            Console.Error.WriteLine("Seed data does not match its manifest. Restore it with 'git checkout -- data/seed'.");
            return 1;
        }

        var foundry = configuration.GetSection(FoundryOptions.SectionName).Get<FoundryOptions>() ?? new();
        var seed = SeedDataSet.Load(paths.SeedDirectory);
        new LocalDemoState(paths.SeedDirectory, paths.LocalStateDirectory).EnsureInitialized();
        var tools = new CostToServeTools(
            seed, new CostEngine(seed.WeeklyInputs), caller,
            new CsvDecisionLog(paths.LocalStateDirectory), new ConsoleDecisionConfirmer(Console.In, Console.Out),
            actions: ActionCatalog.LoadOrDefault(paths.ActionConfigFile));

        AIAgent agent;
        try
        {
            // Local terminal tool: use the Azure CLI sign-in directly (no managed-identity probing).
            agent = CostToServeAgentFactory.Create(foundry, tools, seed, new AzureCliCredential());
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var summaries = trace ? new SpanSummaries() : null;
        using var tracerProvider = ChatTelemetry.Create(summaries);
        var session = await agent.CreateSessionAsync();

        Console.WriteLine($"Cost-to-Serve agent | {caller.DisplayName}, {caller.Title} | role {caller.Role} | scope {caller.ScopeDescription}");
        Console.WriteLine($"Model: {foundry.Deployment} | seed {manifest.Version} | tracing {(trace ? "on" : "off")} | illustrative data");
        Console.WriteLine("Local dev persona (no sign-in until Entra is added). Type /exit to quit.");

        var interactive = questions.Count == 0;
        var queue = new Queue<string>(questions);
        while (true)
        {
            string? input;
            if (interactive)
            {
                Console.Write($"\n{caller.DisplayName}> ");
                input = Console.ReadLine();
                if (input is null)
                {
                    break;
                }
            }
            else
            {
                if (queue.Count == 0)
                {
                    break;
                }

                input = queue.Dequeue();
                Console.WriteLine($"\n{caller.DisplayName}> {input}");
            }

            input = input.Trim();
            if (input.Length == 0)
            {
                continue;
            }

            if (input is "/exit" or "/quit")
            {
                break;
            }

            using var turn = ChatTelemetry.Source.StartActivity("chat_turn");
            turn?.SetTag("app.caller.role", caller.Role.ToString());
            Console.Write("Agent> ");
            try
            {
                await foreach (var update in agent.RunStreamingAsync(input, session))
                {
                    Console.Write(update.Text);
                }

                Console.WriteLine();
            }
            catch (AuthenticationFailedException)
            {
                Console.Error.WriteLine("\nCould not get an Entra token. Run 'az login' and try again.");
                return 1;
            }
            catch (ClientResultException ex)
            {
                // The message is service-controlled; report only the status.
                Console.Error.WriteLine($"\nThe model request failed (HTTP {ex.Status}). Check Foundry access and quota.");
                if (!interactive)
                {
                    return 1;
                }
            }

            if (turn is not null && summaries is not null)
            {
                turn.Stop();
                summaries.Flush(Console.Error, turn.TraceId);
            }
        }

        return 0;
    }
}

/// <summary>Terminal confirmation: the human types 'yes' to log a decision.</summary>
internal sealed class ConsoleDecisionConfirmer(TextReader input, TextWriter output) : IDecisionConfirmer
{
    public Task<bool> ConfirmAsync(DecisionProposal proposal, CancellationToken cancellationToken)
    {
        output.WriteLine();
        output.WriteLine("+-- Confirm decision -------------------------------------------");
        output.WriteLine($"| {proposal.Actor}: {proposal.Decision.ToString().ToUpperInvariant()} {proposal.ActionId} - {proposal.ActionName}"
            + (proposal.Region is { } region ? $" ({region})" : string.Empty));
        if (proposal.Setting is { } setting)
        {
            output.WriteLine($"| Setting: {setting}");
        }

        if (proposal.Reason.Length > 0)
        {
            output.WriteLine($"| Reason: {proposal.Reason}");
        }

        if (proposal.EstimatedWeeklySavingUsd is { } saving)
        {
            output.WriteLine($"| Modeled weekly saving: ${saving:N0} (illustrative)");
        }

        foreach (var note in proposal.GuardrailNotes)
        {
            output.WriteLine($"| Guardrail: {note}");
        }

        output.Write("+-- Type 'yes' to log it, anything else to cancel: ");
        var answer = input.ReadLine()?.Trim();
        output.WriteLine(answer is null ? "(no input)" : string.Empty);
        return Task.FromResult(string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase));
    }
}

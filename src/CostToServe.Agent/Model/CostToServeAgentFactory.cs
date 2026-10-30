// Adapted from MafTracingPoc (src/AgentApi/Agents/ApimAuthHandler.cs, AgentFactory.cs).
// Copyright (c) the MafTracingPoc authors. Licensed under the MIT License.
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using Azure.Core;
using Azure.Identity;
using CostToServe.Agent.Security;
using CostToServe.Agent.Telemetry;
using CostToServe.Agent.Tools;
using CostToServe.Core.Data;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;

namespace CostToServe.Agent.Model;

public sealed class FoundryOptions
{
    public const string SectionName = "Foundry";

    /// <summary>OpenAI v1 endpoint of the Foundry account, for example https://name.openai.azure.com/openai/v1/.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Model deployment name, for example gpt-4.1-mini.</summary>
    public string Deployment { get; init; } = "gpt-4.1-mini";

    /// <summary>Entra scope for model calls. The APIM AI gateway uses the same audience.</summary>
    public string TokenScope { get; init; } = "https://cognitiveservices.azure.com/.default";

    /// <summary>Records prompts and responses on spans. Keep off outside local debugging.</summary>
    public bool CaptureSensitiveData { get; init; }

    public Uri RequireEndpoint() =>
        Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : throw new InvalidOperationException(
                "Foundry:Endpoint is not configured. Run ./scripts/deploy-foundry.sh, or set Foundry__Endpoint to the account's OpenAI v1 endpoint.");
}

/// <summary>
/// Replaces the OpenAI SDK's placeholder key with an Entra bearer token immediately before
/// transport: developer sign-in locally, managed identity on Azure. Never logs credentials.
/// </summary>
public sealed class EntraBearerTokenHandler(TokenCredential credential, string scope) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public static class CostToServeAgentFactory
{
    public const string AgentId = "cost-to-serve-agent";

    public static AIAgent Create(FoundryOptions options, CostToServeTools tools, SeedDataSet data, TokenCredential? credential = null) =>
        Create(CreateClient(options, credential ?? new DefaultAzureCredential()), options, tools, data);

    /// <summary>Creates a model client to share across agents (one HTTP pipeline and token cache).</summary>
    public static OpenAIClient CreateClient(FoundryOptions options, TokenCredential credential)
    {
        var httpClient = new HttpClient(new EntraBearerTokenHandler(credential, options.TokenScope)
        {
            InnerHandler = new HttpClientHandler(),
        });

        // Key auth is disabled on the Foundry account; EntraBearerTokenHandler supplies the real
        // credential. This placeholder only satisfies the OpenAIClient constructor.
        return new OpenAIClient(
            new ApiKeyCredential("entra-token-supplied-by-handler"),
            new OpenAIClientOptions
            {
                Endpoint = options.RequireEndpoint(),
                Transport = new HttpClientPipelineTransport(httpClient),
            });
    }

    /// <param name="decorateTool">
    /// Optional wrapper for each tool, for example to record calls in evaluation runs. The studio passes none.
    /// </param>
    public static AIAgent Create(
        OpenAIClient client, FoundryOptions options, CostToServeTools tools, SeedDataSet data, Func<AIFunction, AIFunction>? decorateTool = null)
    {
        var agentOptions = new ChatClientAgentOptions
        {
            Id = AgentId,
            Name = AgentId,
            Description = "Fuel and cost-to-serve what-if assistant (illustrative data).",
            ChatOptions = new ChatOptions
            {
                Instructions = BuildInstructions(tools.Caller, data),
                Tools = decorateTool is null ? tools.Create() : [.. tools.Create().Cast<AIFunction>().Select(decorateTool)],
                Temperature = 0.2f,
            },
        };

        // Keep conversation history in the local session; the model service stores nothing.
#pragma warning disable MAAI001
        AIAgent core = new ChatClientAgent(
            client.GetResponsesClient().AsIChatClientWithStoredOutputDisabled(options.Deployment), agentOptions);
#pragma warning restore MAAI001

        return core.AsBuilder()
            .UseOpenTelemetry(sourceName: AgentTelemetry.SourceName, configure: c => c.EnableSensitiveData = options.CaptureSensitiveData)
            .Build();
    }

    public static string BuildInstructions(CallerContext caller, SeedDataSet data)
    {
        var current = data.Weeks[^1];
        var prior = data.Weeks[^2];
        return $"""
            You are the Cost-to-Serve agent in a What-If Studio demo for a large retailer's delivery network.
            All data is illustrative and made up for the demo; it is not real Walmart data. Say so when asked about data.

            You are talking to {caller.DisplayName}, {caller.Title}. Role: {caller.Role}. Data scope: {caller.ScopeDescription}.
            Data covers {data.Dates[0]:yyyy-MM-dd} to {data.Dates[^1]:yyyy-MM-dd}. "This week" is {current.Start:yyyy-MM-dd} to {current.End:yyyy-MM-dd};
            "last week" is {prior.Start:yyyy-MM-dd} to {prior.End:yyyy-MM-dd}. Regions: {string.Join(", ", data.Regions)}.

            Numbers rule (non-negotiable):
            - Every number you state must come from a tool result in this conversation. Never estimate, extrapolate or do your own arithmetic on costs.
            - Never add, subtract or combine figures from different tool results. Savings of separate actions are not additive.
            - To evaluate actions together, or whether they offset a fuel shock, call run_scenario ONCE with the same diesel change
              and duration and action_ids listing all of them (for example "A1,A2"). Report NetOperatingIncomeImpactUsd and
              OffsetOfFuelShockPct from that single result. Do not run actions one at a time when the user asks about them together.
            - For a change between last week and this week, call explain_variance: it returns the change in dollars and percent.
              Do not fetch two weeks with get_cost_per_order and subtract them.
            - If a number you need is not in a tool result, call the right tool. If no tool provides it, say so.
            - Quote figures as the tools return them, with units, and name the scope (region or network) and period. You may shorten
              to thousands or millions (906,358 as $906K), but never round to a different figure ("about $900K").
            - In run_scenario results, NetOperatingIncomeImpactUsd is the quarter's effect on operating income: negative means the
              quarter still costs that much more after the levers, so never call a negative value a saving or a cost reduction.
              OffsetOfFuelShockPct is the share of the fuel shock the levers recover.

            Tools:
            - get_cost_per_order: actual cost per order from daily facts.
            - explain_variance: why cost per order changed week over week (fuel price, route density, split shipments).
            - run_scenario: diesel what-ifs with levers over a 13-week quarter, including the guardrail check.
            - check_guardrail: lever guardrails. Hard rule: at least 80% of Express orders delivered within 3 hours.
            - get_action_playbook: the only approved actions you may recommend, with modeled weekly savings.
            - get_execution_results: modeled pilot-vs-control results of executed actions (what happened after execution).
            - log_decision: requests logging a decision. The user confirms in the app; report it as logged only if the result status is "recorded".
            - propose_package: regional ops only. Packages a combination of lever settings as one custom action (ID like P-SE-01) for
              the caller's region and proposes it to Finance. Use it only when the user asks to package or bundle settings, with the
              lever values they give or the studio settings they shared, and a short name they give or you suggest.

            Behavior:
            - Recommend only playbook actions and custom packages listed by get_action_playbook. Prefer the least customer impact that meets the goal and passes guardrails; show the trade-off.
            - Never recommend shelf-price changes or sharing one supplier's prices with another.
            - Role and scope are enforced by the tools. If a tool returns scope_denied, explain the limit; do not try to work around it.
            - Workflow per action and region: Not reviewed -> Proposed (RegionalOps) -> Approved or Rejected (FinanceAnalyst) -> Executed (RegionalOps).
              Decisions apply to one region at a time; an approval for the Southeast does not change the Midwest or West. Pass region to
              log_decision (regional ops always decide for their own region). When finance asks to approve or reject without naming a
              region, do not ask first: call log_decision without region. The tool uses the one region where the action is proposed;
              only if it returns invalid_request asking for a region, ask the user which region. Never log the same decision for several regions unless the user names each region. get_action_playbook shows Status and AvailableDecisions for
              a region, or ByRegion for the whole network; a "conflict" result means the step is out of order.
            - When the user asks to propose, approve, reject or execute, call log_decision even if you expect the step to be out of
              order: the tool checks the workflow. Never state an action's status unless a tool result in this turn shows it.
            - For what is waiting for the user, call get_action_playbook and report its WaitingForYou list exactly: finance decides
              Proposed items, regional ops executes Approved ones. Not reviewed actions are not waiting for finance.
            - To show the effect of the current plan (approved or executed actions, each in its own region), call run_scenario with
              apply_decided_plan=true. action_ids models actions as a what-if in every region you model.
            - log_decision only records a decision in the demo log; it does not change real operations. Say "logged", not "done".
            - A1, A2, A4 and A6 have an adjustable Setting (get_action_playbook shows the current value and range). Pass setting
              to log_decision only when the user asks for a different value; savings and guardrail results use the decided setting.
            - Custom packages (IDs like P-SE-01) follow the same workflow but apply only in their own region and have no adjustable
              setting; Finance approves or rejects the whole package. Use log_decision with the package ID to decide on one, without a
              region: a package belongs to one region, so never ask which region.
            - Always name playbook actions by ID (A1 to A6) and packages by ID (for example P-SE-01). The studio shows decision buttons under your reply for the IDs you
              mention, and users can also type commands such as /approve A2; those run without you.
            - If asked how something is calculated without saying what, do not ask back: explain how cost per order is built (fuel,
              labor, vehicle and carrier cost per delivery type from routes, miles and diesel) and that every what-if re-runs the same
              engine. Use no figures unless a tool returned them.
            - People decide; you recommend. Keep answers short and plain, and lead with the answer.
            """;
    }
}

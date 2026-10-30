using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Azure.Identity;
using CostToServe.Agent.Decisions;
using CostToServe.Agent.Evidence;
using CostToServe.Agent.Security;
using CostToServe.Core.Data;
using CostToServe.Core.Engine;
using CostToServe.Core.Model;
using Microsoft.Extensions.AI;

namespace CostToServe.Api.Studio;

public sealed record ChatRequest(string? ConversationId, string Message);

public sealed record ConfirmationRequest(bool Confirm);

public sealed record ScenarioRequest(double DieselShift, int DurationWeeks, Levers? Levers, string? ActionIds, string? Region, bool ApplyPlan = false);

public sealed record GuardrailRequest(Levers? Levers, string? ActionIds, string? Region = null);

public sealed record DecisionRequest(string ActionId, string Decision, string? Reason, double? Setting = null, string? Region = null);

/// <summary>Regional ops packages lever settings as one custom action and proposes it to Finance.</summary>
public sealed record PackageRequest(string Name, Levers Levers, string? Reason = null);

public static class StudioEndpoints
{
    public const string PersonaHeader = "X-Demo-Persona";
    private const string CallerKey = "cts.caller";
    private const int MaxMessageLength = 2000;

    // SSE frames must be single-line JSON; the shared AI defaults are indented.
    private static readonly JsonSerializerOptions SseJson = new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };

    /// <summary>
    /// Maps the studio API. With Entra configured, the caller comes only from a validated access
    /// token and its app roles. Otherwise, in local development, the X-Demo-Persona header may
    /// identify an illustrative persona. With neither, every request is rejected.
    /// </summary>
    public static void MapStudio(this WebApplication app, EntraOptions entra, bool devPersonasEnabled, System.Text.Json.Nodes.JsonNode? evaluationSummary = null)
    {
        var authMode = entra.IsConfigured ? "entra" : devPersonasEnabled ? "devPersonas" : "none";

        app.MapGet("/api/health", (StudioServices s) => new
        {
            Status = "ok",
            s.DataMode,
            IllustrativeData = true,
            s.SeedVersion,
            ChatConfigured = s.ChatConfigured,
            AuthMode = authMode,
        });

        // Public sign-in settings for MSAL in the browser (client and tenant IDs are not secrets).
        app.MapGet("/api/auth-config", () => entra.IsConfigured
            ? Results.Json(new { Mode = authMode, entra.TenantId, entra.ClientId, Scopes = new[] { entra.ApiScope } })
            : Results.Json(new { Mode = authMode }));

        var api = app.MapGroup("/api").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            CallerContext? caller = null;
            if (entra.IsConfigured)
            {
                var services = http.RequestServices.GetRequiredService<StudioServices>();
                var (result, mapped) = EntraCallerMapper.Map(http.User, entra, services.Data.Regions);
                switch (result)
                {
                    case CallerResolution.NotSignedIn:
                        return Results.Problem("Sign in to use the studio.", statusCode: StatusCodes.Status401Unauthorized, title: "Sign-in required");
                    case CallerResolution.NoRole:
                        return Results.Problem("Your account has no studio role. Ask an administrator to assign FinanceAnalyst or RegionalOps.",
                            statusCode: StatusCodes.Status403Forbidden, title: "No role assigned");
                    case CallerResolution.NoRegion:
                        return Results.Problem("Your RegionalOps role has no region configured, so no data is available.",
                            statusCode: StatusCodes.Status403Forbidden, title: "No region assigned");
                }

                caller = mapped;
            }
            else if (devPersonasEnabled && http.Request.Headers.TryGetValue(PersonaHeader, out var persona))
            {
                try
                {
                    caller = DemoPersonas.Parse(persona.ToString());
                }
                catch (ArgumentException)
                {
                    caller = null;
                }
            }

            if (caller is null)
            {
                return Results.Problem(
                    devPersonasEnabled ? $"Send the {PersonaHeader} header (dana or luis)." : "Sign-in is not configured.",
                    statusCode: StatusCodes.Status401Unauthorized, title: "Caller not identified");
            }

            http.Items[CallerKey] = caller;
            Activity.Current?.SetTag("app.caller.role", caller.Role.ToString());
            try
            {
                return await next(context);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message.Split('\n')[0], statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
            }
        });

        api.MapGet("/me", (HttpContext http, StudioServices s) =>
        {
            var caller = Caller(http);
            return new
            {
                caller.DisplayName,
                caller.Title,
                Role = caller.Role.ToString(),
                Scope = caller.ScopeDescription,
                caller.CanApprove,
                Regions = s.Data.Regions.Where(caller.CanSee).ToArray(),
                AuthMode = authMode,
            };
        });

        api.MapGet("/dashboard", (HttpContext http, StudioServices s) => Dashboard(Caller(http), s));

        api.MapPost("/scenario", (HttpContext http, StudioServices s, ScenarioRequest r) =>
            ToResult(s.ToolsFor(Caller(http)).RunScenario(r.DieselShift, r.DurationWeeks, r.Region, r.ActionIds, r.ApplyPlan, r.Levers ?? Levers.None)));

        api.MapPost("/guardrail", (HttpContext http, StudioServices s, GuardrailRequest r) =>
            ToResult(s.ToolsFor(Caller(http)).CheckGuardrail(r.ActionIds, r.Region, r.Levers ?? Levers.None)));

        api.MapGet("/playbook", (HttpContext http, StudioServices s, string? region) =>
            ToResult(s.ToolsFor(Caller(http)).GetActionPlaybook(region)));

        api.MapGet("/cost-per-order", (HttpContext http, StudioServices s, string? region, string? zone, string? deliveryType, string? from, string? to) =>
            ToResult(s.ToolsFor(Caller(http)).GetCostPerOrder(region, zone, deliveryType, from, to)));

        api.MapGet("/variance", (HttpContext http, StudioServices s, string region, string deliveryType) =>
            ToResult(s.ToolsFor(Caller(http)).ExplainVariance(region, deliveryType)));

        api.MapGet("/results", (HttpContext http, StudioServices s) => ToResult(s.ToolsFor(Caller(http)).GetExecutionResults()));

        api.MapGet("/trust", (HttpContext http, StudioServices s, TrustSpanStore spans, TrustOptions options) =>
            Results.Json(TrustEndpoints.Build(Caller(http), s, spans, options, evaluationSummary), AIJsonUtilities.DefaultOptions));

        api.MapGet("/decisions", (StudioServices s) => s.DecisionLog.ReadAll()
            .OrderByDescending(d => d.Timestamp)
            .Select(d => new { Timestamp = d.Timestamp.ToString("yyyy-MM-ddTHH:mm:ssZ"), d.ActionId, d.Actor, Decision = d.Decision.ToString().ToLowerInvariant(), d.Reason, d.Setting, Region = d.EffectiveRegion }));

        api.MapPost("/decisions", async (HttpContext http, StudioServices s, DecisionRequest r, CancellationToken ct) =>
            ToResult(await s.ToolsFor(Caller(http)).LogDecisionAsync(r.ActionId, r.Decision, r.Reason, r.Setting, r.Region, ct)));

        api.MapPost("/packages", async (HttpContext http, StudioServices s, PackageRequest r, CancellationToken ct) =>
            ToResult(await s.ToolsFor(Caller(http)).ProposePackageAsync(r.Name, r.Reason, r.Levers ?? Levers.None, ct)));

        api.MapPost("/confirmations/{id}", (HttpContext http, StudioServices s, string id, ConfirmationRequest r) =>
            s.Confirmations.Resolve(id, Caller(http).Id, r.Confirm) ? Results.NoContent() : Results.NotFound());

        api.MapPost("/chat", (HttpContext http, StudioServices s, ChatRequest r, CancellationToken ct) => ChatAsync(http, s, r, ct));
    }

    private static CallerContext Caller(HttpContext http) => (CallerContext)http.Items[CallerKey]!;

    /// <summary>The user's question without the studio settings the browser may append as context.</summary>
    public static string VisibleQuestion(string message)
    {
        var cut = message.IndexOf("\n\n[Studio settings for reference:", StringComparison.Ordinal);
        return cut >= 0 ? message[..cut] : message;
    }

    /// <summary>Maps tool error objects to HTTP status codes; successful results pass through unchanged.</summary>
    private static IResult ToResult(object toolResult)
    {
        var json = JsonSerializer.SerializeToElement(toolResult, AIJsonUtilities.DefaultOptions);
        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var error))
        {
            var message = json.TryGetProperty("message", out var m) ? m.GetString() : null;
            return error.GetString() switch
            {
                "scope_denied" => Results.Problem(message, statusCode: StatusCodes.Status403Forbidden, title: "Outside your scope"),
                "conflict" => Results.Problem(message, statusCode: StatusCodes.Status409Conflict, title: "Not allowed in the current state"),
                _ => Results.Problem(message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request"),
            };
        }

        return Results.Json(json);
    }

    private static async Task ChatAsync(HttpContext http, StudioServices s, ChatRequest request, CancellationToken ct)
    {
        var caller = Caller(http);
        var message = request.Message?.Trim() ?? string.Empty;
        if (message.Length is 0 or > MaxMessageLength)
        {
            await Results.Problem($"Message must be 1 to {MaxMessageLength} characters.", statusCode: 400).ExecuteAsync(http);
            return;
        }

        if (!s.ChatConfigured)
        {
            await Results.Problem("Foundry is not configured. Run ./scripts/deploy-foundry.sh.", statusCode: 503).ExecuteAsync(http);
            return;
        }

        var conversation = string.IsNullOrWhiteSpace(request.ConversationId)
            ? s.StartConversation(caller)
            : s.Conversations.Get(request.ConversationId, caller.Id);
        if (conversation is null)
        {
            await Results.Problem("Conversation not found. Start a new one.", statusCode: 404).ExecuteAsync(http);
            return;
        }

        if (!await conversation.Gate.WaitAsync(0, ct))
        {
            await Results.Problem("A reply is still in progress.", statusCode: 409).ExecuteAsync(http);
            return;
        }

        var traceId = Activity.Current?.TraceId.ToHexString();
        var channel = Channel.CreateUnbounded<StudioEvent>(new UnboundedChannelOptions { SingleReader = true });
        conversation.Relay.Attach(channel.Writer);
        conversation.LastUsed = DateTimeOffset.UtcNow;

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        async Task ProduceAsync()
        {
            var watch = Stopwatch.StartNew();
            var answer = new System.Text.StringBuilder();
            long? inputTokens = null, outputTokens = null;
            void RecordFailure() => s.Trust.Add(new TrustTurn(
                caller.Id, caller.DisplayName, DateTimeOffset.UtcNow, VisibleQuestion(message), [],
                new AnswerEvidence([]), watch.ElapsedMilliseconds, inputTokens, outputTokens, traceId, Errored: true));
            lock (conversation.TurnTools)
            {
                conversation.TurnTools.Clear();
            }

            try
            {
                channel.Writer.TryWrite(new("start", new { conversationId = conversation.Id }));
                conversation.Session ??= await conversation.Agent.CreateSessionAsync(ct);
                await foreach (var update in conversation.Agent.RunStreamingAsync(message, conversation.Session, cancellationToken: ct))
                {
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        answer.Append(update.Text);
                        channel.Writer.TryWrite(new("delta", new { text = update.Text }));
                    }

                    foreach (var usage in update.Contents.OfType<UsageContent>())
                    {
                        inputTokens = (inputTokens ?? 0) + (usage.Details.InputTokenCount ?? 0);
                        outputTokens = (outputTokens ?? 0) + (usage.Details.OutputTokenCount ?? 0);
                    }
                }

                // Live evidence: every number in the answer is traced to a tool result, the question or the
                // instructions. The same check as the offline evaluation; no model call.
                TurnToolCall[] calls;
                lock (conversation.TurnTools)
                {
                    calls = [.. conversation.TurnTools];
                }

                var evidence = NumberEvidence.Trace(answer.ToString(), calls.Select(c => (c.Name, c.Result)), message, conversation.Instructions);
                channel.Writer.TryWrite(new("evidence", new
                {
                    quoted = evidence.Quoted,
                    traced = evidence.Traced,
                    unsupported = evidence.Unsupported,
                    numbers = evidence.Numbers.Select(n => new { text = n.Text, source = n.Source }),
                    tools = calls.Select(c => new { name = c.Name, outcome = c.Outcome, message = c.Message, decision = c.DecisionStatus }),
                    durationMs = watch.ElapsedMilliseconds,
                    traceId,
                }));
                s.Trust.Add(new TrustTurn(
                    caller.Id, caller.DisplayName, DateTimeOffset.UtcNow, VisibleQuestion(message),
                    calls.Select(c => (c.Name, c.Outcome, c.Message, c.DecisionStatus)).ToArray(),
                    evidence, watch.ElapsedMilliseconds, inputTokens, outputTokens, traceId, Errored: false));
            }
            catch (OperationCanceledException)
            {
                // The browser disconnected.
            }
            catch (AuthenticationFailedException)
            {
                RecordFailure();
                channel.Writer.TryWrite(new("error", new { message = "Could not get an Entra token for the model. Run 'az login'." }));
            }
            catch (ClientResultException ex) when (ContentSafety.IsBlocked(ex))
            {
                // The platform's content filter stopped the prompt before the agent ran: a refusal by policy, not an error.
                channel.Writer.TryWrite(new("delta", new { text = ContentSafety.Answer }));
                var blocked = new { name = ContentSafety.ToolName, outcome = ContentSafety.Outcome, message = ContentSafety.Message, decision = (string?)null };
                channel.Writer.TryWrite(new("evidence", new
                {
                    quoted = 0,
                    traced = 0,
                    unsupported = Array.Empty<string>(),
                    numbers = Array.Empty<object>(),
                    tools = new[] { blocked },
                    durationMs = watch.ElapsedMilliseconds,
                    traceId,
                }));
                s.Trust.Add(new TrustTurn(
                    caller.Id, caller.DisplayName, DateTimeOffset.UtcNow, VisibleQuestion(message),
                    [(ContentSafety.ToolName, ContentSafety.Outcome, ContentSafety.Message, null)],
                    new AnswerEvidence([]), watch.ElapsedMilliseconds, inputTokens, outputTokens, traceId, Errored: false));
            }
            catch (ClientResultException ex)
            {
                // The message is service-controlled; report only the status.
                RecordFailure();
                channel.Writer.TryWrite(new("error", new { message = $"The model request failed (HTTP {ex.Status})." }));
            }
            finally
            {
                channel.Writer.TryWrite(new("done", new { conversationId = conversation.Id, traceId }));
                conversation.Relay.Detach();
                channel.Writer.TryComplete();
                conversation.Gate.Release();
            }
        }

        var producer = ProduceAsync();
        var reply = new System.Text.StringBuilder();
        var tools = new List<string>();
        string? error = null;
        try
        {
            await foreach (var e in channel.Reader.ReadAllAsync(ct))
            {
                var payload = JsonSerializer.SerializeToElement(e.Data, SseJson);
                switch (e.Type)
                {
                    case "delta" when payload.TryGetProperty("text", out var text):
                        reply.Append(text.GetString());
                        break;
                    case "tool" when payload.TryGetProperty("name", out var name):
                        tools.Add(name.GetString() ?? "?");
                        break;
                    case "error" when payload.TryGetProperty("message", out var m):
                        error = m.GetString();
                        break;
                }

                await http.Response.WriteAsync($"event: {e.Type}\ndata: {payload.GetRawText()}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The browser disconnected; the producer stops via the same token.
        }

        await producer;

        if (s.Transcripts is { } transcripts)
        {
            try
            {
                await transcripts.SaveAsync(
                    new TranscriptTurn(conversation.Id, caller.Id, caller.Role.ToString(), message, reply.ToString(), tools, traceId, error, DateTimeOffset.UtcNow),
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                // A transcript is for evaluations; failing to keep one must not fail the chat.
                http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("CostToServe.Transcripts")
                    .LogWarning("Could not save the chat transcript: {Error}", ex.GetType().Name);
            }
        }
    }

    private static object Dashboard(CallerContext caller, StudioServices s)
    {
        var data = s.Data;
        var current = data.Weeks[^1];
        var prior = data.Weeks[^2];
        var regions = data.Regions.Where(caller.CanSee).ToArray();

        double? Facts(string? region, DeliveryType type, DemoWeek week)
        {
            var f = data.QueryCostPerOrder(new CostQuery(region, DeliveryType: type, From: week.Start, To: week.End));
            return f.Orders > 0 ? Math.Round(f.CostPerOrder, 2) : null;
        }

        var scopes = caller.IsNetworkWide ? regions.Prepend("Network") : regions;
        var costPerOrder = scopes.SelectMany(scope => DeliveryTypes.All.Select(type =>
        {
            var region = scope == "Network" ? null : scope;
            var before = Facts(region, type, prior);
            var after = Facts(region, type, current);
            return new
            {
                Scope = scope,
                DeliveryType = type.ToSeedName(),
                PriorUsd = before,
                CurrentUsd = after,
                ChangePct = before is > 0 && after is not null ? Math.Round((after.Value / before.Value - 1) * 100, 1) : (double?)null,
            };
        })).ToArray();

        return new
        {
            Caller = new { caller.DisplayName, caller.Title, Role = caller.Role.ToString(), Scope = caller.ScopeDescription, caller.CanApprove },
            Period = new
            {
                DataStart = data.Dates[0].ToString("yyyy-MM-dd"),
                DataEnd = data.Dates[^1].ToString("yyyy-MM-dd"),
                CurrentWeek = $"{current.Start:yyyy-MM-dd} to {current.End:yyyy-MM-dd}",
                PriorWeek = $"{prior.Start:yyyy-MM-dd} to {prior.End:yyyy-MM-dd}",
            },
            s.SeedVersion,
            Regions = regions,
            Fuel = regions.Select(r => new
            {
                Region = r,
                CurrentWeekDiesel = Math.Round(data.WeeklyInputs.Get(current.Number, r, DeliveryType.Express).Diesel, 3),
                PriorWeekDiesel = Math.Round(data.WeeklyInputs.Get(prior.Number, r, DeliveryType.Express).Diesel, 3),
                Daily = data.Fuel.Where(f => f.Region == r).OrderBy(f => f.Date)
                    .Select(f => new { Date = f.Date.ToString("yyyy-MM-dd"), Diesel = f.DieselUsdPerGallon }),
                Outlook = data.FuelOutlook.Where(o => o.Region == r).OrderBy(o => o.WeekStart)
                    .Select(o => new { WeekStart = o.WeekStart.ToString("yyyy-MM-dd"), Low = o.DieselLow, Base = o.DieselBase, High = o.DieselHigh }),
            }),
            Events = data.MarketEvents.Where(e => e.Region == "All" || caller.CanSee(e.Region)).OrderByDescending(e => e.Date)
                .Select(e => new { Date = e.Date.ToString("yyyy-MM-dd"), e.EventType, e.Region, Effect = e.ExpectedDieselEffectUsdPerGallon, e.Credibility }),
            CostPerOrder = costPerOrder,
            // Presets and suggested questions from the reference demo (docs/reference). Diesel changes add to this
            // week's price, which already includes the Oct 19 jump, so the descriptions say "another".
            Presets = new[]
            {
                new { Id = "calm", Label = "Calm market", DieselShift = -0.15, DurationWeeks = 13, Description = "Diesel eases about $0.15/gal from this week's price for the quarter." },
                new { Id = "refinery", Label = "Refinery outage", DieselShift = 0.45, DurationWeeks = 6, Description = "Another +$0.45/gal for 6 weeks if the Southeast refinery outage drags on (high-credibility sources)." },
                new { Id = "conflict", Label = "Shipping disruption", DieselShift = 0.8, DurationWeeks = 10, Description = "Another +$0.80/gal for 10 weeks if the crude-shipping disruption escalates (stress case; medium credibility)." },
            },
            Suggestions = new[]
            {
                "What's driving the Southeast Express cost increase, and is it just fuel?",
                "What if diesel rises $0.50 for 8 weeks?",
                "Which playbook actions offset it with the least customer impact?",
                "Do A1 plus A2 together keep the Express guardrail?",
                "How is this calculated?",
            },
            LeverLimits = new
            {
                LeverLimits.BatchMax, LeverLimits.CloserMax, LeverLimits.SplitMax, LeverLimits.PickupMax,
                LeverLimits.FeeMin, LeverLimits.FeeMax, LeverLimits.DieselShiftMin, LeverLimits.DieselShiftMax,
                LeverLimits.DurationMinWeeks, LeverLimits.DurationMaxWeeks,
            },
            Actions = s.Actions.All.Select(a => new { a.Id, a.Levers, a.LeversDerivedFromPlaybookText, a.Source }),
        };
    }
}

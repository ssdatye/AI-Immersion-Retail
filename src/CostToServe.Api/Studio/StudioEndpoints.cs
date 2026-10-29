using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Azure.Identity;
using CostToServe.Agent.Decisions;
using CostToServe.Agent.Security;
using CostToServe.Core.Data;
using CostToServe.Core.Engine;
using CostToServe.Core.Model;
using Microsoft.Extensions.AI;

namespace CostToServe.Api.Studio;

public sealed record ChatRequest(string? ConversationId, string Message);

public sealed record ConfirmationRequest(bool Confirm);

public sealed record ScenarioRequest(double DieselShift, int DurationWeeks, Levers? Levers, string? ActionIds, string? Region);

public sealed record GuardrailRequest(Levers? Levers, string? ActionIds);

public sealed record DecisionRequest(string ActionId, string Decision, string? Reason, double? Setting = null);

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
    public static void MapStudio(this WebApplication app, EntraOptions entra, bool devPersonasEnabled)
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
            ToResult(s.ToolsFor(Caller(http)).RunScenario(r.DieselShift, r.DurationWeeks, r.Region, r.ActionIds, r.Levers ?? Levers.None)));

        api.MapPost("/guardrail", (HttpContext http, StudioServices s, GuardrailRequest r) =>
            ToResult(s.ToolsFor(Caller(http)).CheckGuardrail(r.ActionIds, r.Levers ?? Levers.None)));

        api.MapGet("/playbook", (HttpContext http, StudioServices s, string? region) =>
            ToResult(s.ToolsFor(Caller(http)).GetActionPlaybook(region)));

        api.MapGet("/cost-per-order", (HttpContext http, StudioServices s, string? region, string? zone, string? deliveryType, string? from, string? to) =>
            ToResult(s.ToolsFor(Caller(http)).GetCostPerOrder(region, zone, deliveryType, from, to)));

        api.MapGet("/variance", (HttpContext http, StudioServices s, string region, string deliveryType) =>
            ToResult(s.ToolsFor(Caller(http)).ExplainVariance(region, deliveryType)));

        api.MapGet("/results", (HttpContext http, StudioServices s) => ToResult(s.ToolsFor(Caller(http)).GetExecutionResults()));

        api.MapGet("/decisions", (StudioServices s) => s.DecisionLog.ReadAll()
            .OrderByDescending(d => d.Timestamp)
            .Select(d => new { Timestamp = d.Timestamp.ToString("yyyy-MM-ddTHH:mm:ssZ"), d.ActionId, d.Actor, Decision = d.Decision.ToString().ToLowerInvariant(), d.Reason, d.Setting }));

        api.MapPost("/decisions", async (HttpContext http, StudioServices s, DecisionRequest r, CancellationToken ct) =>
            ToResult(await s.ToolsFor(Caller(http)).LogDecisionAsync(r.ActionId, r.Decision, r.Reason, r.Setting, ct)));

        api.MapPost("/confirmations/{id}", (HttpContext http, StudioServices s, string id, ConfirmationRequest r) =>
            s.Confirmations.Resolve(id, Caller(http).Id, r.Confirm) ? Results.NoContent() : Results.NotFound());

        api.MapPost("/chat", (HttpContext http, StudioServices s, ChatRequest r, CancellationToken ct) => ChatAsync(http, s, r, ct));
    }

    private static CallerContext Caller(HttpContext http) => (CallerContext)http.Items[CallerKey]!;

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
            try
            {
                channel.Writer.TryWrite(new("start", new { conversationId = conversation.Id }));
                conversation.Session ??= await conversation.Agent.CreateSessionAsync(ct);
                await foreach (var update in conversation.Agent.RunStreamingAsync(message, conversation.Session, cancellationToken: ct))
                {
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        channel.Writer.TryWrite(new("delta", new { text = update.Text }));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The browser disconnected.
            }
            catch (AuthenticationFailedException)
            {
                channel.Writer.TryWrite(new("error", new { message = "Could not get an Entra token for the model. Run 'az login'." }));
            }
            catch (ClientResultException ex)
            {
                // The message is service-controlled; report only the status.
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
            // Presets and suggested questions from the reference demo (docs/reference).
            Presets = new[]
            {
                new { Id = "calm", Label = "Calm market", DieselShift = -0.15, DurationWeeks = 13, Description = "Diesel eases about $0.15/gal for the quarter." },
                new { Id = "refinery", Label = "Refinery outage", DieselShift = 0.45, DurationWeeks = 6, Description = "About +$0.45/gal for 6 weeks (high-credibility sources)." },
                new { Id = "conflict", Label = "Shipping disruption", DieselShift = 0.8, DurationWeeks = 10, Description = "About +$0.80/gal for 10 weeks (medium credibility; developing)." },
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

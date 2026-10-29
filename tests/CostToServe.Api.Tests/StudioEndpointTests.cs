using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CostToServe.Agent.Decisions;
using CostToServe.Agent.Security;
using CostToServe.Api.Studio;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CostToServe.Api.Tests;

/// <summary>Runs the real API in memory with dev personas on. No model calls.</summary>
public sealed class StudioFactory() : StudioFactoryBase(devPersonas: true);

public sealed class StudioEndpointTests : IClassFixture<StudioFactory>
{
    private readonly StudioFactory _factory;

    public StudioEndpointTests(StudioFactory factory) => _factory = factory;

    [Fact]
    public async Task Requests_without_a_persona_are_unauthorized()
    {
        var response = await _factory.CreateClient().GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_personas_are_unauthorized()
    {
        var response = await _factory.As("mallory").GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Luis_sees_only_the_southeast()
    {
        var luis = _factory.As("luis");

        var dashboard = await luis.GetFromJsonAsync<JsonElement>("/api/dashboard");
        var midwest = await luis.GetAsync("/api/cost-per-order?region=Midwest");
        var scenario = await (await luis.PostAsJsonAsync("/api/scenario", new { dieselShift = 0.5, durationWeeks = 13, region = "West" })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(["Southeast"], dashboard.GetProperty("regions").EnumerateArray().Select(r => r.GetString()));
        Assert.All(dashboard.GetProperty("costPerOrder").EnumerateArray(), row => Assert.Equal("Southeast", row.GetProperty("scope").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, midwest.StatusCode);
        Assert.Equal("Outside your scope", scenario.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Dana_gets_the_network_scenario_from_the_same_engine_as_the_agent()
    {
        var response = await _factory.As("dana").PostAsJsonAsync("/api/scenario", new { dieselShift = 0.5, durationWeeks = 13, actionIds = "A1,A2" });
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("Network", result.GetProperty("scope").GetString());
        Assert.Equal(88.1, result.GetProperty("offsetOfFuelShockPct").GetDouble());
        Assert.Equal(28.2, Math.Round(result.GetProperty("fuelShockQuarterCostUsd").GetDouble() / 1e6, 1));
    }

    [Fact]
    public async Task Invalid_levers_return_bad_request()
    {
        var response = await _factory.As("dana").PostAsJsonAsync("/api/scenario", new { dieselShift = 0.5, durationWeeks = 13, levers = new { batch = 0.9 } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Decision_buttons_follow_roles_and_write_to_the_configured_state_directory()
    {
        var luisApproves = await _factory.As("luis").PostAsJsonAsync("/api/decisions", new { actionId = "A4", decision = "approve" });
        var luisExecutesUnapproved = await _factory.As("luis").PostAsJsonAsync("/api/decisions", new { actionId = "A4", decision = "execute" });
        var danaApproves = await _factory.As("dana").PostAsJsonAsync("/api/decisions", new { actionId = "A4", decision = "approve", reason = "test" });
        var luisExecutes = await _factory.As("luis").PostAsJsonAsync("/api/decisions", new { actionId = "A4", decision = "execute" });

        Assert.Equal(HttpStatusCode.Forbidden, luisApproves.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, luisExecutesUnapproved.StatusCode);
        Assert.Equal(HttpStatusCode.OK, danaApproves.StatusCode);
        Assert.Equal(HttpStatusCode.OK, luisExecutes.StatusCode);

        var log = File.ReadAllText(Path.Combine(_factory.StateDirectory, "local", "decision_log.csv"));
        Assert.Contains(",A4,Dana,approve,test,", log);
        Assert.Contains(",A4,Luis,execute,,", log);
    }

    [Fact]
    public async Task Decisions_carry_an_adjustable_setting_into_the_log_and_the_playbook()
    {
        var propose = await _factory.As("luis").PostAsJsonAsync("/api/decisions", new { actionId = "A6", decision = "propose", setting = 0.05 });
        var tooHigh = await _factory.As("dana").PostAsJsonAsync("/api/decisions", new { actionId = "A6", decision = "approve", setting = 0.5 });
        var approve = await _factory.As("dana").PostAsJsonAsync("/api/decisions", new { actionId = "A6", decision = "approve" });

        Assert.Equal(HttpStatusCode.OK, propose.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooHigh.StatusCode);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var decisions = await _factory.As("dana").GetFromJsonAsync<JsonElement>("/api/decisions");
        Assert.Contains(decisions.EnumerateArray(), d => d.GetProperty("actionId").GetString() == "A6"
            && d.GetProperty("decision").GetString() == "approve" && d.GetProperty("setting").GetDouble() == 0.05);
        var playbook = await _factory.As("dana").GetFromJsonAsync<JsonElement>("/api/playbook");
        var a6 = playbook.GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("actionId").GetString() == "A6");
        Assert.Equal("Shift to Pickup 5%", a6.GetProperty("setting").GetProperty("text").GetString());
        Assert.Equal(0.05, a6.GetProperty("levers").GetProperty("pickup").GetDouble());
    }

    [Fact]
    public async Task Only_the_owner_can_resolve_a_confirmation()
    {
        var services = (StudioServices)_factory.Services.GetService(typeof(StudioServices))!;
        var (id, result) = services.Confirmations.Register(DemoPersonas.Dana.Id);

        var byLuis = await _factory.As("luis").PostAsJsonAsync($"/api/confirmations/{id}", new { confirm = true });
        var byDana = await _factory.As("dana").PostAsJsonAsync($"/api/confirmations/{id}", new { confirm = true });
        var again = await _factory.As("dana").PostAsJsonAsync($"/api/confirmations/{id}", new { confirm = false });

        Assert.Equal(HttpStatusCode.NotFound, byLuis.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, byDana.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.True(await result);
    }

    [Fact]
    public async Task Chat_rejects_empty_messages_and_unknown_conversations_without_calling_the_model()
    {
        var dana = _factory.As("dana");

        var empty = await dana.PostAsJsonAsync("/api/chat", new { message = " " });
        var unknown = await dana.PostAsJsonAsync("/api/chat", new { conversationId = "nope", message = "hi" });

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unknown.StatusCode);
    }

    [Fact]
    public async Task Unknown_api_routes_return_not_found()
    {
        var response = await _factory.As("dana").GetAsync("/api/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

public sealed class DevPersonasDisabledTests : IClassFixture<DevPersonasDisabledTests.Factory>
{
    private readonly Factory _factory;

    public DevPersonasDisabledTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Persona_header_is_ignored_when_dev_personas_are_off()
    {
        var response = await _factory.As("dana").GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public sealed class Factory() : StudioFactoryBase(devPersonas: false);
}

/// <summary>Runs the real API in memory with a temporary demo-state directory.</summary>
public abstract class StudioFactoryBase(bool devPersonas) : WebApplicationFactory<Program>
{
    public string StateDirectory { get; } = Directory.CreateTempSubdirectory("cts-api-tests-").FullName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("CostToServe:LocalStateDirectory", Path.Combine(StateDirectory, "local"));
        builder.UseSetting("CostToServe:DevPersonas", devPersonas ? "true" : "false");

        // Hermetic: ignore the developer's git-ignored local settings (Foundry, Entra).
        builder.UseSetting("CostToServe:LoadLocalSettings", "false");
        ConfigureTestHost(builder);
    }

    protected virtual void ConfigureTestHost(IWebHostBuilder builder)
    {
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(StateDirectory))
        {
            Directory.Delete(StateDirectory, recursive: true);
        }
    }

    public HttpClient As(string persona)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(StudioEndpoints.PersonaHeader, persona);
        return client;
    }
}

public sealed class WebDecisionConfirmerTests
{
    [Fact]
    public async Task Emits_a_card_and_returns_the_users_choice()
    {
        var pending = new PendingConfirmations();
        var relay = new EventRelay();
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StudioEvent>();
        relay.Attach(channel.Writer);
        var confirmer = new WebDecisionConfirmer(pending, relay, "Dana");
        var proposal = new DecisionProposal("A1", "Batch Express", DecisionKind.Approve, "Dana", "", 1000, []);

        var waiting = confirmer.ConfirmAsync(proposal, CancellationToken.None);
        var card = await channel.Reader.ReadAsync();
        var id = JsonSerializer.SerializeToElement(card.Data).GetProperty("id").GetString()!;
        Assert.Equal("confirmation", card.Type);
        Assert.False(pending.Resolve(id, "Luis", true));
        Assert.True(pending.Resolve(id, "Dana", false));

        Assert.False(await waiting);
        Assert.Equal("confirmation_closed", (await channel.Reader.ReadAsync()).Type);
    }

    [Fact]
    public async Task Cancellation_counts_as_declined()
    {
        var confirmer = new WebDecisionConfirmer(new PendingConfirmations(), new EventRelay(), "Dana");
        using var cts = new CancellationTokenSource();
        var waiting = confirmer.ConfirmAsync(new DecisionProposal("A1", "x", DecisionKind.Approve, "Dana", "", null, []), cts.Token);

        await cts.CancelAsync();

        Assert.False(await waiting);
    }
}

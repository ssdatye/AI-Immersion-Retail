using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CostToServe.Api.Studio;

namespace CostToServe.Api.Tests;

/// <summary>The Trust Center API and its portal links. No model calls.</summary>
public sealed class TrustCenterTests : IClassFixture<StudioFactory>
{
    private readonly StudioFactory _factory;

    public TrustCenterTests(StudioFactory factory) => _factory = factory;

    [Fact]
    public async Task Trust_requires_a_signed_in_caller()
    {
        var response = await _factory.CreateClient().GetAsync("/api/trust");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Trust_returns_the_evaluation_summary_and_an_empty_session_before_any_chat()
    {
        var trust = await _factory.As("dana").GetFromJsonAsync<JsonElement>("/api/trust");

        var evaluation = trust.GetProperty("evaluation");
        Assert.True(evaluation.GetProperty("cases").GetInt32() > 0);
        Assert.Equal(evaluation.GetProperty("cases").GetInt32(), evaluation.GetProperty("passedAllChecks").GetInt32());
        Assert.Equal(0, evaluation.GetProperty("numbersUnsupported").GetInt32());

        var session = trust.GetProperty("session");
        Assert.Equal(0, session.GetProperty("answers").GetInt32());
        Assert.Equal(0, session.GetProperty("numbersToReview").GetInt32());
        Assert.Empty(trust.GetProperty("turns").EnumerateArray());
        Assert.False(trust.TryGetProperty("lastTrace", out var last) && last.ValueKind != JsonValueKind.Null);
        Assert.Equal("Microsoft.DefaultV2", trust.GetProperty("controls").GetProperty("contentFilter").GetString());
        Assert.Equal(0, session.GetProperty("allUsers").GetProperty("answers").GetInt32());
        Assert.Equal("/architecture", trust.GetProperty("links").GetProperty("architecture").GetString());
    }

    [Fact]
    public async Task Evaluation_summary_lists_every_question_with_its_result()
    {
        var evaluation = (await _factory.As("luis").GetFromJsonAsync<JsonElement>("/api/trust")).GetProperty("evaluation");

        var rows = evaluation.GetProperty("caseRows").EnumerateArray().ToArray();
        Assert.Equal(evaluation.GetProperty("cases").GetInt32(), rows.Length);
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("question").GetString())));
        Assert.Contains(rows, r => r.GetProperty("refusalByDesign").GetBoolean());
    }

    [Fact]
    public async Task Trust_prices_tokens_and_projects_a_monthly_cost_from_the_evaluation_run()
    {
        var trust = await _factory.As("dana").GetFromJsonAsync<JsonElement>("/api/trust");

        var pricing = trust.GetProperty("pricing");
        Assert.Equal(0.40, pricing.GetProperty("inputPerMillionUsd").GetDouble());
        Assert.Equal(1.60, pricing.GetProperty("outputPerMillionUsd").GetDouble());
        var tokens = trust.GetProperty("evaluation").GetProperty("tokens");
        var expected = new TokenPricing().Cost(tokens.GetProperty("input").GetInt64(), tokens.GetProperty("cachedInput").GetInt64(), tokens.GetProperty("output").GetInt64());
        Assert.Equal(expected, pricing.GetProperty("evaluationCostUsd").GetDouble(), 9);
        var projection = pricing.GetProperty("projection");
        var perAnswer = expected / trust.GetProperty("evaluation").GetProperty("cases").GetInt32();
        Assert.Equal(perAnswer * 1000 * 30, projection.GetProperty("monthlyUsd").GetDouble(), 6);
        Assert.Equal(0, trust.GetProperty("session").GetProperty("costUsd").GetDouble());
    }

    [Fact]
    public void Cached_input_is_billed_at_the_cached_rate()
    {
        var pricing = new TokenPricing();

        Assert.Equal(0.0026, Math.Round(pricing.Cost(6_400, 0, 25), 6));
        Assert.Equal((1_000 * 0.40 + 1_000 * 0.10 + 100 * 1.60) / 1_000_000, pricing.Cost(2_000, 1_000, 100), 12);
        Assert.Equal(0, pricing.Cost(null, null, null));
    }

    [Fact]
    public async Task Architecture_document_is_served_without_sign_in()
    {
        var response = await _factory.CreateClient().GetAsync("/architecture");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public void Foundry_link_encodes_the_subscription_and_names_the_project()
    {
        var options = new TrustOptions
        {
            FoundryProjectResourceId =
                "/subscriptions/00112233-4455-6677-8899-aabbccddeeff/resourceGroups/rg-demo/providers/Microsoft.CognitiveServices/accounts/acct/projects/proj-evals",
        };

        Assert.Equal("https://ai.azure.com/nextgen/r/ABEiM0RVZneImaq7zN3u_w,rg-demo,,acct,proj-evals/build/evaluations", options.FoundryEvaluationsUrl());
        Assert.Equal(
            "https://ai.azure.com/nextgen/r/ABEiM0RVZneImaq7zN3u_w,rg-demo,,acct,proj-evals/build/evaluations/eval_1/run/evalrun_2",
            options.FoundryRunUrl("eval_1", "evalrun_2"));
        Assert.Null(options.FoundryRunUrl(null, "evalrun_2"));
        Assert.Null(new TrustOptions { FoundryProjectResourceId = "/subscriptions/not-a-guid" }.FoundryEvaluationsUrl());
        Assert.Null(new TrustOptions().AppInsightsSearchUrl());
    }

    [Theory]
    [InlineData("invoke_agent cost-to-serve", ActivityKind.Internal, "agent")]
    [InlineData("chat gpt-4.1-mini", ActivityKind.Client, "model")]
    [InlineData("execute_tool explain_variance", ActivityKind.Internal, "tool")]
    [InlineData("execute_tool log_decision", ActivityKind.Internal, "person")]
    [InlineData("POST /api/chat", ActivityKind.Server, "request")]
    [InlineData("GET /api/dashboard", ActivityKind.Server, null)]
    public void Spans_are_classified_for_the_timeline(string name, ActivityKind kind, string? expected)
    {
        using var activity = new Activity(name);
        activity.DisplayName = name;
        typeof(Activity).GetProperty(nameof(Activity.Kind))!.SetValue(activity, kind);

        Assert.Equal(expected, TrustSpanStore.Classify(activity));
    }

    [Fact]
    public void Visible_question_drops_the_studio_settings_the_browser_appends()
    {
        Assert.Equal("Why did cost per order rise?", StudioEndpoints.VisibleQuestion("Why did cost per order rise?\n\n[Studio settings for reference: diesel +0.50]"));
        Assert.Equal("Approve A1", StudioEndpoints.VisibleQuestion("Approve A1"));
    }
}

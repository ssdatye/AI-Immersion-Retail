using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CostToServe.Api.Tests;

/// <summary>POST /api/packages: regional ops packages lever settings and proposes them to Finance in one click.</summary>
public sealed class PackageEndpointTests : IClassFixture<PackageEndpointTests.Factory>
{
    private readonly Factory _factory;

    public PackageEndpointTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Luis_proposes_a_package_that_Dana_sees_and_approves()
    {
        var created = await _factory.As("luis").PostAsJsonAsync("/api/packages",
            new { name = "Southeast fuel squeeze", reason = "offset the shock", levers = new { batch = 0.4, closer = 0.3, fee = 1.0, cap = true } });
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("actionId").GetString()!;

        var playbook = await _factory.As("dana").GetFromJsonAsync<JsonElement>("/api/playbook?region=Southeast");
        var row = playbook.GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("actionId").GetString() == id);
        var approve = await _factory.As("dana").PostAsJsonAsync("/api/decisions", new { actionId = id, decision = "approve" });

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Matches("^P-SE-\\d{2}$", id);
        Assert.True(row.GetProperty("isPackage").GetBoolean());
        Assert.Equal("Proposed", row.GetProperty("status").GetString());
        Assert.Contains("approve", row.GetProperty("availableDecisions").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        Assert.True(File.Exists(Path.Combine(_factory.StateDirectory, "local", "packages.jsonl")));
    }

    [Fact]
    public async Task Finance_cannot_create_packages_and_bad_input_is_rejected()
    {
        var dana = await _factory.As("dana").PostAsJsonAsync("/api/packages", new { name = "Network package", levers = new { batch = 0.3 } });
        var empty = await _factory.As("luis").PostAsJsonAsync("/api/packages", new { name = "Nothing", levers = new { } });
        var outOfRange = await _factory.As("luis").PostAsJsonAsync("/api/packages", new { name = "Too much", levers = new { batch = 0.9 } });

        Assert.Equal(HttpStatusCode.Forbidden, dana.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, outOfRange.StatusCode);
    }

    public sealed class Factory() : StudioFactoryBase(devPersonas: true);
}

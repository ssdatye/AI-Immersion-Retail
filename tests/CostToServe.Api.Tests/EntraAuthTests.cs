using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using CostToServe.Api.Studio;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CostToServe.Api.Tests;

/// <summary>Entra mode with test-signed tokens: no network, no real tenant.</summary>
public sealed class EntraFactory() : StudioFactoryBase(devPersonas: true)
{
    public const string TenantId = "11111111-1111-1111-1111-111111111111";
    public const string ClientId = "22222222-2222-2222-2222-222222222222";
    public const string DanaOid = "aaaaaaaa-0000-0000-0000-000000000001";
    public const string LuisOid = "aaaaaaaa-0000-0000-0000-000000000002";
    public const string UnmappedOpsOid = "aaaaaaaa-0000-0000-0000-000000000003";
    public const string NoRoleOid = "aaaaaaaa-0000-0000-0000-000000000004";

    private static readonly SymmetricSecurityKey Key = new(new byte[32]
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
    });

    public static string Issuer => $"https://login.microsoftonline.com/{TenantId}/v2.0";

    protected override void ConfigureTestHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Entra:TenantId", TenantId);
        builder.UseSetting("Entra:ClientId", ClientId);
        builder.UseSetting($"Entra:RegionalOpsRegions:{LuisOid}", "southeast");
        builder.ConfigureTestServices(services =>
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                // Use a local signing key instead of downloading tenant metadata.
                o.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                o.Configuration.SigningKeys.Add(Key);
                o.TokenValidationParameters.IssuerSigningKey = Key;
            }));
    }

    public static string Token(string oid, string name, string[] roles, string? audience = null, string? issuer = null)
    {
        var claims = new Dictionary<string, object> { ["oid"] = oid, ["name"] = name };
        if (roles.Length > 0)
        {
            claims["roles"] = roles;
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer,
            Audience = audience ?? ClientId,
            Claims = claims,
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
        });
    }

    public HttpClient WithToken(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

public sealed class EntraAuthTests(EntraFactory factory) : IClassFixture<EntraFactory>
{
    [Fact]
    public async Task Auth_config_tells_the_browser_to_use_entra()
    {
        var config = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/auth-config");

        Assert.Equal("entra", config.GetProperty("mode").GetString());
        Assert.Equal(EntraFactory.ClientId, config.GetProperty("clientId").GetString());
        Assert.Equal($"api://{EntraFactory.ClientId}/access_as_user", config.GetProperty("scopes")[0].GetString());
    }

    [Fact]
    public async Task Finance_analyst_token_gets_the_network_view()
    {
        var me = await factory.WithToken(EntraFactory.Token(EntraFactory.DanaOid, "Dana Demo", ["FinanceAnalyst"]))
            .GetFromJsonAsync<JsonElement>("/api/me");

        Assert.Equal("FinanceAnalyst", me.GetProperty("role").GetString());
        Assert.True(me.GetProperty("canApprove").GetBoolean());
        Assert.Equal(3, me.GetProperty("regions").GetArrayLength());
        Assert.Equal("Dana Demo", me.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Regional_ops_token_is_limited_to_the_configured_region()
    {
        var luis = factory.WithToken(EntraFactory.Token(EntraFactory.LuisOid, "Luis Demo", ["RegionalOps"]));

        var me = await luis.GetFromJsonAsync<JsonElement>("/api/me");
        var midwest = await luis.GetAsync("/api/cost-per-order?region=Midwest");

        Assert.Equal("RegionalOps", me.GetProperty("role").GetString());
        Assert.Equal(["Southeast"], me.GetProperty("regions").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, midwest.StatusCode);
    }

    [Fact]
    public async Task Both_roles_resolve_to_finance_only()
    {
        var me = await factory.WithToken(EntraFactory.Token(EntraFactory.LuisOid, "Both", ["RegionalOps", "FinanceAnalyst"]))
            .GetFromJsonAsync<JsonElement>("/api/me");

        Assert.Equal("FinanceAnalyst", me.GetProperty("role").GetString());
    }

    [Theory]
    [InlineData(EntraFactory.UnmappedOpsOid, new[] { "RegionalOps" }, "No region assigned")]
    [InlineData(EntraFactory.NoRoleOid, new string[0], "No role assigned")]
    [InlineData(EntraFactory.NoRoleOid, new[] { "Admin" }, "No role assigned")]
    public async Task Signed_in_users_without_a_usable_role_are_forbidden(string oid, string[] roles, string title)
    {
        var response = await factory.WithToken(EntraFactory.Token(oid, "Someone", roles)).GetAsync("/api/dashboard");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(title, problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Invalid_tokens_and_persona_headers_are_rejected()
    {
        var wrongAudience = await factory.WithToken(EntraFactory.Token(EntraFactory.DanaOid, "Dana", ["FinanceAnalyst"], audience: "api://someone-else"))
            .GetAsync("/api/me");
        var wrongIssuer = await factory.WithToken(EntraFactory.Token(EntraFactory.DanaOid, "Dana", ["FinanceAnalyst"], issuer: "https://evil.example/v2.0"))
            .GetAsync("/api/me");
        var personaClient = factory.CreateClient();
        personaClient.DefaultRequestHeaders.Add(StudioEndpoints.PersonaHeader, "dana");
        var persona = await personaClient.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongAudience.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongIssuer.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, persona.StatusCode);
    }

    [Fact]
    public async Task Confirmations_are_owned_by_the_object_id()
    {
        var services = (StudioServices)factory.Services.GetService(typeof(StudioServices))!;
        var (id, result) = services.Confirmations.Register(EntraFactory.DanaOid);

        var otherFinanceUser = await factory.WithToken(EntraFactory.Token(EntraFactory.NoRoleOid.Replace('4', '5'), "Dana", ["FinanceAnalyst"]))
            .PostAsJsonAsync($"/api/confirmations/{id}", new { confirm = true });
        var dana = await factory.WithToken(EntraFactory.Token(EntraFactory.DanaOid, "Dana", ["FinanceAnalyst"]))
            .PostAsJsonAsync($"/api/confirmations/{id}", new { confirm = true });

        Assert.Equal(HttpStatusCode.NotFound, otherFinanceUser.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, dana.StatusCode);
        Assert.True(await result);
    }

    [Fact]
    public void Mapper_ignores_unauthenticated_principals()
    {
        var (result, caller) = EntraCallerMapper.Map(new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "x"), new Claim("roles", "FinanceAnalyst")])),
            new EntraOptions { TenantId = "t", ClientId = "c" }, ["Southeast"]);

        Assert.Equal(CallerResolution.NotSignedIn, result);
        Assert.Null(caller);
    }
}

public sealed class EntraOptionsTests
{
    [Fact]
    public void Regions_come_from_the_keyed_map_or_the_single_value_setting()
    {
        var options = new EntraOptions
        {
            RegionalOpsRegions = new(StringComparer.OrdinalIgnoreCase) { ["aaaa-1"] = "West" },
            RegionalOpsAssignments = " bbbb-2 = Southeast ; cccc-3= ;bad",
        };

        Assert.Equal("West", options.RegionFor("AAAA-1"));
        Assert.Equal("Southeast", options.RegionFor("bbbb-2"));
        Assert.Null(options.RegionFor("cccc-3"));
        Assert.Null(options.RegionFor("dddd-4"));
    }
}

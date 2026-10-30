using System.ClientModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Identity;
using CostToServe.Agent.Model;
using CostToServe.Core.Data;
using CostToServe.Core.Engine;
using CostToServe.Evals;

// Runs the evaluation dataset against the agent and writes results for the Foundry step and the report.
// Usage: dotnet run --project evals/CostToServe.Evals -- [--dataset evals/dataset.jsonl] [--out evals/out/<run>] [--case id ...]
var repo = FindRepoRoot(Directory.GetCurrentDirectory()) ?? FindRepoRoot(AppContext.BaseDirectory)
    ?? throw new InvalidOperationException("Run from inside the repository (CostToServe.slnx not found).");
var dataset = Path.Combine(repo, "evals", "dataset.jsonl");
var runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
string? outDir = null;
var only = new HashSet<string>(StringComparer.Ordinal);
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--dataset" when i + 1 < args.Length: dataset = Path.GetFullPath(args[++i]); break;
        case "--out" when i + 1 < args.Length: outDir = Path.GetFullPath(args[++i]); break;
        case "--case" when i + 1 < args.Length: only.Add(args[++i]); break;
        default:
            Console.Error.WriteLine($"Unknown argument '{args[i]}'. Usage: [--dataset file] [--out dir] [--case id ...]");
            return 2;
    }
}

outDir ??= Path.Combine(repo, "evals", "out", runId);
var seedDir = Path.Combine(repo, "data", "seed");
var manifest = SeedManifest.Verify(seedDir);
if (!manifest.IsValid)
{
    Console.Error.WriteLine("Seed data does not match its manifest; refusing to evaluate.");
    return 1;
}

var foundry = LoadFoundryOptions(repo);
var cases = EvalCase.Load(dataset).Where(c => only.Count == 0 || only.Contains(c.Id)).ToArray();
var data = SeedDataSet.Load(seedDir);
var actions = ActionCatalog.LoadOrDefault(Path.Combine(repo, "data", "config", "action-levers.csv"));
var client = CostToServeAgentFactory.CreateClient(foundry, new AzureCliCredential());
var runner = new EvalRunner(data, actions, client, foundry);

Directory.CreateDirectory(outDir);
var resultsPath = Path.Combine(outDir, "results.jsonl");
Console.WriteLine($"Evaluating {cases.Length} cases with {foundry.Deployment} (seed {manifest.Version}). Results: {resultsPath}");

var results = new List<JsonObject>();
await using (var writer = new StreamWriter(resultsPath))
{
    foreach (var c in cases)
    {
        JsonObject? result = null;
        for (var attempt = 1; result is null; attempt++)
        {
            try
            {
                result = await runner.RunAsync(c, CancellationToken.None);
            }
            catch (ClientResultException ex) when (ex.Status == 429 && attempt < 4)
            {
                Console.WriteLine($"  {c.Id}: rate limited, retrying in 30 seconds");
                await Task.Delay(TimeSpan.FromSeconds(30));
            }
        }

        results.Add(result);
        await writer.WriteLineAsync(result.ToJsonString());
        var checks = result["checks"]!.AsObject();
        string Mark(string key) => checks[key]?["result"]?.GetValue<string>() switch { "pass" => "ok", "fail" => "FAIL", _ => "-" };
        Console.WriteLine(
            $"{(result["result"]!.GetValue<string>() == "fail" ? "✗" : "✓")} {c.Id,-30} tools {Mark("tool_selection"),-4} numbers {Mark("numeric_accuracy"),-4} " +
            $"grounded {Mark("grounded_numbers"),-4} decision {Mark("decision"),-4} ({result["duration_ms"]} ms)");
    }
}

var summary = new JsonObject
{
    ["run_id"] = Path.GetFileName(outDir),
    ["created_utc"] = DateTime.UtcNow.ToString("O"),
    ["model"] = foundry.Deployment,
    ["seed_version"] = manifest.Version,
    ["dataset_sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(dataset))),
    ["cases"] = results.Count,
    ["passed"] = results.Count(r => r["result"]!.GetValue<string>() == "pass"),
    ["failed"] = results.Count(r => r["result"]!.GetValue<string>() == "fail"),
    ["input_tokens"] = results.Sum(r => r["input_tokens"]?.GetValue<long>() ?? 0),
    ["output_tokens"] = results.Sum(r => r["output_tokens"]?.GetValue<long>() ?? 0),
    ["cached_input_tokens"] = results.Sum(r => r["cached_input_tokens"]?.GetValue<long>() ?? 0),
};
await File.WriteAllTextAsync(Path.Combine(outDir, "run.json"), summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Deterministic checks: {summary["passed"]}/{summary["cases"]} cases passed. Run folder: {outDir}");
return 0;

static string? FindRepoRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "CostToServe.slnx")))
        {
            return dir.FullName;
        }
    }

    return null;
}

// The local model endpoint and deployment, from git-ignored local settings or Foundry__* environment variables.
static FoundryOptions LoadFoundryOptions(string repo)
{
    string? endpoint = null, deployment = null;
    var local = Path.Combine(repo, "src", "CostToServe.Api", "appsettings.Local.json");
    if (File.Exists(local) && JsonNode.Parse(File.ReadAllText(local))?["Foundry"] is JsonObject f)
    {
        endpoint = (string?)f["Endpoint"];
        deployment = (string?)f["Deployment"];
    }

    endpoint = Environment.GetEnvironmentVariable("Foundry__Endpoint") ?? endpoint;
    deployment = Environment.GetEnvironmentVariable("Foundry__Deployment") ?? deployment;
    var options = new FoundryOptions { Endpoint = endpoint, Deployment = deployment ?? "gpt-4.1-mini" };
    options.RequireEndpoint();
    return options;
}

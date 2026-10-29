using System.Globalization;
using Azure.Core;
using CostToServe.Agent.Decisions;
using CostToServe.Api.Studio;
using Microsoft.Azure.Cosmos;

namespace CostToServe.Api.Cloud;

/// <summary>
/// Cosmos DB (serverless, Entra-only) for mutable demo state: <c>decisions</c> (partition
/// <c>/actionId</c>) and <c>threads</c> (chat transcripts, partition <c>/ownerId</c>). The
/// <c>scenarios</c> container is provisioned for saved scenarios and evaluation runs.
/// </summary>
public sealed class CosmosStores : IDisposable
{
    public const string DecisionsContainer = "decisions";
    public const string ThreadsContainer = "threads";
    public const string ScenariosContainer = "scenarios";

    private readonly CosmosClient _client;

    private CosmosStores(CosmosClient client, Database database)
    {
        _client = client;
        Decisions = database.GetContainer(DecisionsContainer);
        Threads = database.GetContainer(ThreadsContainer);
    }

    public Container Decisions { get; }

    public Container Threads { get; }

    public static async Task<CosmosStores> ConnectAsync(CosmosOptions options, TokenCredential credential, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            throw new InvalidOperationException("CostToServe:Cosmos:Endpoint is not configured.");
        }

        var client = new CosmosClient(options.Endpoint, credential, new CosmosClientOptions
        {
            ApplicationName = "cost-to-serve-studio",
            SerializerOptions = new CosmosSerializationOptions { PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase },
        });
        var database = client.GetDatabase(options.Database);
        // Fail at startup, not on the first click, if the account or role assignment is wrong.
        await database.ReadAsync(cancellationToken: ct).ConfigureAwait(false);
        return new CosmosStores(client, database);
    }

    /// <summary>Deletes every decision and transcript. Destructive for the demo's Cosmos data only.</summary>
    public async Task<(int Decisions, int Threads)> ClearAsync(CancellationToken ct = default) =>
        (await ClearAsync(Decisions, "actionId", ct).ConfigureAwait(false), await ClearAsync(Threads, "ownerId", ct).ConfigureAwait(false));

    private static async Task<int> ClearAsync(Container container, string partitionKey, CancellationToken ct)
    {
        var count = 0;
        using var feed = container.GetItemQueryIterator<Dictionary<string, object>>($"SELECT c.id, c.{partitionKey} FROM c");
        while (feed.HasMoreResults)
        {
            foreach (var item in await feed.ReadNextAsync(ct).ConfigureAwait(false))
            {
                await container.DeleteItemAsync<object>(item["id"].ToString(), new PartitionKey(item[partitionKey].ToString()), cancellationToken: ct)
                    .ConfigureAwait(false);
                count++;
            }
        }

        return count;
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// Decision log in Cosmos, read through on every call so changes made outside this instance
/// (another instance, or ./scripts/reset-demo.sh --target azure) show up on the next request.
/// The log is a few dozen small items, so a read costs a few request units.
/// </summary>
public sealed class CosmosDecisionLog(Container container) : IDecisionLog
{
    public static Task<CosmosDecisionLog> LoadAsync(CosmosStores stores, CancellationToken ct = default) =>
        Task.FromResult(new CosmosDecisionLog(stores.Decisions));

    // IDecisionLog is synchronous; ASP.NET Core has no synchronization context, so blocking cannot deadlock.
    public IReadOnlyList<DecisionEntry> ReadAll() => ReadAllAsync(CancellationToken.None).GetAwaiter().GetResult();

    public async Task<IReadOnlyList<DecisionEntry>> ReadAllAsync(CancellationToken ct)
    {
        var entries = new List<DecisionEntry>();
        using var feed = container.GetItemQueryIterator<DecisionDocument>("SELECT * FROM c");
        while (feed.HasMoreResults)
        {
            entries.AddRange((await feed.ReadNextAsync(ct).ConfigureAwait(false)).Select(d => d.ToEntry()));
        }

        return entries.OrderBy(e => e.Timestamp).ToArray();
    }

    public void Append(DecisionEntry entry)
    {
        var doc = DecisionDocument.From(entry);
        container.CreateItemAsync(doc, new PartitionKey(doc.ActionId)).GetAwaiter().GetResult();
    }

    public sealed class DecisionDocument
    {
        public string Id { get; set; } = "";

        public string ActionId { get; set; } = "";

        public string Timestamp { get; set; } = "";

        public string Actor { get; set; } = "";

        public string Decision { get; set; } = "";

        public string Reason { get; set; } = "";

        public double? Setting { get; set; }

        public string? Region { get; set; }

        public static DecisionDocument From(DecisionEntry e) => new()
        {
            Id = Guid.NewGuid().ToString("n"),
            ActionId = e.ActionId,
            Timestamp = e.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Actor = e.Actor,
            Decision = e.Decision.ToString().ToLowerInvariant(),
            Reason = e.Reason,
            Setting = e.Setting,
            Region = e.Region,
        };

        public DecisionEntry ToEntry() => new(
            DateTimeOffset.Parse(Timestamp, CultureInfo.InvariantCulture),
            ActionId,
            Actor,
            Enum.Parse<DecisionKind>(Decision, ignoreCase: true),
            Reason,
            Setting,
            Region);
    }
}

/// <summary>Chat transcripts in the Cosmos <c>threads</c> container, one item per turn.</summary>
public sealed class CosmosTranscriptStore(CosmosStores stores) : ITranscriptStore
{
    public Task SaveAsync(TranscriptTurn turn, CancellationToken cancellationToken) =>
        stores.Threads.CreateItemAsync(new
        {
            id = Guid.NewGuid().ToString("n"),
            ownerId = turn.OwnerId,
            ownerRole = turn.OwnerRole,
            conversationId = turn.ConversationId,
            userMessage = turn.UserMessage,
            reply = turn.Reply,
            tools = turn.Tools,
            traceId = turn.TraceId,
            error = turn.Error,
            timestamp = turn.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        }, new PartitionKey(turn.OwnerId), cancellationToken: cancellationToken);
}

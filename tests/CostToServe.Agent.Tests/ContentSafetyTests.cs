using System.ClientModel;
using System.ClientModel.Primitives;
using CostToServe.Agent.Security;

namespace CostToServe.Agent.Tests;

/// <summary>A request the model deployment's content filter blocks is a refusal by policy, not a model failure.</summary>
public sealed class ContentSafetyTests
{
    [Fact]
    public void A_content_filter_rejection_is_recognized()
    {
        var blocked = new ClientResultException("HTTP 400 (invalid_request_error: content_filter)", new Response(400));

        Assert.True(ContentSafety.IsBlocked(blocked));
    }

    [Fact]
    public void Other_model_errors_are_not_treated_as_blocks()
    {
        Assert.False(ContentSafety.IsBlocked(new ClientResultException("HTTP 400 (invalid_request_error: bad schema)", new Response(400))));
        Assert.False(ContentSafety.IsBlocked(new ClientResultException("HTTP 429 (rate_limit: content_filter quota)", new Response(429))));
    }

    private sealed class Response(int status) : PipelineResponse
    {
        public override int Status => status;

        public override string ReasonPhrase => string.Empty;

        public override Stream? ContentStream { get; set; }

        public override BinaryData Content => BinaryData.FromString(string.Empty);

        protected override PipelineResponseHeaders HeadersCore => throw new NotSupportedException();

        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Content;

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => new(Content);

        public override void Dispose()
        {
        }
    }
}

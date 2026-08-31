using System.Net;
using Speech2Issues.Core.Configuration;
using Speech2Issues.Core.Destinations;
using Speech2Issues.Core.Models;

namespace Speech2Issues.Tests;

public sealed class WebhookDestinationTests
{
    [Fact]
    public async Task RetriesTransientFailureWithStableIdempotencyKey()
    {
        var handler = new WebhookHandler();
        using var client = new HttpClient(handler);
        var destination = new WebhookDestination(client, new WebhookSettings { Url = "https://webhook.test/tasks" }, new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer secret",
        });
        var draft = new TaskDraft { Id = "s2i-webhook", Title = "Task" };

        var result = await destination.CreateAsync(draft);

        Assert.Equal(2, handler.Calls);
        Assert.All(handler.Keys, key => Assert.Equal("s2i-webhook", key));
        Assert.True(handler.SawSecret);
        Assert.Equal("s2i-webhook", result.ExternalId);
    }

    private sealed class WebhookHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string?> Keys { get; } = [];
        public bool SawSecret { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Keys.Add(request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null);
            SawSecret |= request.Headers.Authorization?.Parameter == "secret";
            return Task.FromResult(new HttpResponseMessage(Calls == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK));
        }
    }
}

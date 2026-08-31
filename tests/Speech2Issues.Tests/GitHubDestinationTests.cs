using System.Net;
using System.Text;
using Speech2Issues.Core.Configuration;
using Speech2Issues.Core.Destinations;
using Speech2Issues.Core.Models;

namespace Speech2Issues.Tests;

public sealed class GitHubDestinationTests
{
    [Fact]
    public async Task CreatesIssueWithOnlyExistingLabelsAndMarker()
    {
        var handler = new GitHubHandler();
        using var client = new HttpClient(handler);
        var destination = new GitHubDestination(client, new GitHubSettings { Repository = "owner/repo" }, "token");
        var draft = new TaskDraft
        {
            Id = "s2i-github",
            Title = "Issue title",
            Description = "Issue body",
            Labels = ["bug", "invented"],
            Transcript = "source",
        };

        var result = await destination.CreateAsync(draft);

        Assert.Equal("7", result.ExternalId);
        Assert.Contains("speech2issues:s2i-github", handler.CreatedBody);
        Assert.Contains("\"bug\"", handler.CreatedBody);
        Assert.DoesNotContain("invented", handler.CreatedBody);
        Assert.True(handler.SawBearer);
    }

    private sealed class GitHubHandler : HttpMessageHandler
    {
        public string CreatedBody { get; private set; } = string.Empty;
        public bool SawBearer { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SawBearer |= request.Headers.Authorization?.Parameter == "token";
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.EndsWith("/issues")) return Json("[]");
            if (request.Method == HttpMethod.Get && path.EndsWith("/labels")) return Json("[{\"name\":\"bug\"}]");
            if (request.Method == HttpMethod.Post)
            {
                CreatedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json("{\"number\":7,\"html_url\":\"https://github.test/owner/repo/issues/7\"}");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };
    }
}

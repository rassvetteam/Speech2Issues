using System.Net.Http;
using System.Net.Http.Headers;
using Speech2Issues.Core.Configuration;
using Speech2Issues.Core.Destinations;
using Speech2Issues.Core.Models;
using Speech2Issues.Core.Services;

namespace Speech2Issues.App.Services;

public static class DestinationFactory
{
    public static IAiTaskProvider CreateAiProvider(HttpClient client, AppSettings settings) => settings.AiProvider.Kind switch
    {
        AiProviderKind.Ollama => new OllamaService(client, settings.AiProvider.Model, settings.OutputLanguage),
        AiProviderKind.LmStudio => new OpenAiCompatibleService(client, settings.AiProvider.Model, "LM Studio", settings.OutputLanguage),
        AiProviderKind.OpenAiCompatible => new OpenAiCompatibleService(client, settings.AiProvider.Model, "OpenAI-compatible API", settings.OutputLanguage),
        _ => throw new ArgumentOutOfRangeException(nameof(settings.AiProvider.Kind)),
    };

    public static HttpClient CreateAiHttpClient(AppSettings settings, AppSecrets secrets, TimeSpan? timeout = null)
    {
        var client = CreateHttpClient(settings.AiProvider.BaseUrl, timeout);
        if (settings.AiProvider.Kind != AiProviderKind.Ollama && !string.IsNullOrWhiteSpace(secrets.AiApiToken))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secrets.AiApiToken.Trim());
        }
        return client;
    }

    public static ITaskDestination Create(DestinationKind kind, AppSettings settings, AppSecrets secrets) => kind switch
    {
        DestinationKind.Planka => new PlankaDestination(CreateHttpClient(), settings.Planka, secrets.PlankaApiKey),
        DestinationKind.GitHub => new GitHubDestination(CreateHttpClient(), settings.GitHub, secrets.GitHubToken),
        DestinationKind.Obsidian => new ObsidianDestination(settings.Obsidian),
        DestinationKind.Webhook => new WebhookDestination(CreateHttpClient(), settings.Webhook, secrets.WebhookHeaders),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static ITaskDestination Create(DestinationBinding binding, AppSettings settings, AppSecrets secrets, string? resolvedTargetId = null) => binding.Kind switch
    {
        DestinationKind.Planka => new PlankaDestination(
            CreateHttpClient(),
            new PlankaSettings
            {
                BaseUrl = settings.Planka.BaseUrl,
                ListId = resolvedTargetId ?? binding.Planka?.FallbackListId ?? string.Empty,
                TargetDisplayName = binding.DisplayName,
            },
            secrets.PlankaApiKey),
        DestinationKind.GitHub => new GitHubDestination(
            CreateHttpClient(),
            new GitHubSettings { Repository = binding.GitHub?.Repository ?? settings.GitHub.Repository },
            secrets.GitHubToken),
        DestinationKind.Obsidian => new ObsidianDestination(new ObsidianSettings
        {
            VaultPath = settings.Obsidian.VaultPath,
            Profile = settings.Obsidian.Profile,
            ProjectFile = binding.Obsidian?.ProjectFile ?? settings.Obsidian.ProjectFile,
            OutputFolder = binding.Obsidian?.OutputFolder ?? settings.Obsidian.OutputFolder,
            InboxFile = binding.Obsidian?.InboxFile ?? settings.Obsidian.InboxFile,
            TaskNotesStatusField = settings.Obsidian.TaskNotesStatusField,
            TaskNotesPriorityField = settings.Obsidian.TaskNotesPriorityField,
            TaskNotesDueField = settings.Obsidian.TaskNotesDueField,
            TaskNotesStatus = settings.Obsidian.TaskNotesStatus,
            ProjectManagerStatus = settings.Obsidian.ProjectManagerStatus,
        }),
        DestinationKind.Webhook => new WebhookDestination(CreateHttpClient(), settings.Webhook, secrets.WebhookHeaders),
        _ => throw new ArgumentOutOfRangeException(nameof(binding)),
    };

    public static HttpClient CreateHttpClient(string? baseUrl = null, TimeSpan? timeout = null)
    {
        var client = new HttpClient { Timeout = timeout ?? TimeSpan.FromMinutes(6) };
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        }
        return client;
    }
}

using Speech2Issues.Core.Models;
using System.Text.Json.Serialization;

namespace Speech2Issues.Core.Configuration;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 4;
    public int SetupVersion { get; set; }
    public string Theme { get; set; } = "indigo";
    public AiProviderSettings AiProvider { get; set; } = new();
    [JsonPropertyName("ollamaUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyOllamaUrl { get; set; }
    [JsonPropertyName("ollamaModel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyOllamaModel { get; set; }
    public string OutputLanguage { get; set; } = "Russian";
    public DestinationKind DefaultDestination { get; set; } = DestinationKind.Planka;
    public int CountdownSeconds { get; set; } = 5;
    public bool CloseToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public HotKeySettings Hotkey { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public SpeechRecognitionSettings SpeechRecognition { get; set; } = new();
    public PlankaSettings Planka { get; set; } = new();
    public GitHubSettings GitHub { get; set; } = new();
    public ObsidianSettings Obsidian { get; set; } = new();
    public WebhookSettings Webhook { get; set; } = new();
    public string? LastActiveProjectId { get; set; }
    public List<ProjectProfile> Projects { get; set; } = [];
}

public sealed class ProjectProfile
{
    public string Id { get; set; } = $"project-{Guid.NewGuid():N}";
    public string Name { get; set; } = "Новый проект";
    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ExternalProjectLink> ExternalLinks { get; set; } = [];
    public List<DestinationBinding> Destinations { get; set; } = [];
}

public sealed class ExternalProjectLink
{
    public DestinationKind Kind { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class DestinationBinding
{
    public string Id { get; set; } = $"binding-{Guid.NewGuid():N}";
    public DestinationKind Kind { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string DisplayName { get; set; } = string.Empty;
    public PlankaProjectBinding? Planka { get; set; }
    public GitHubProjectBinding? GitHub { get; set; }
    public ObsidianProjectBinding? Obsidian { get; set; }
    public WebhookProjectBinding? Webhook { get; set; }
}

public sealed class PlankaProjectBinding
{
    public string ProjectId { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public List<PlankaTargetBinding> AllowedTargets { get; set; } = [];
    public string FallbackListId { get; set; } = string.Empty;
}

public sealed class PlankaTargetBinding
{
    public string ListId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? BoardId { get; set; }
}

public sealed class GitHubProjectBinding
{
    public string Repository { get; set; } = string.Empty;
}

public sealed class ObsidianProjectBinding
{
    public string ProjectFile { get; set; } = string.Empty;
    public string OutputFolder { get; set; } = "Speech2Issues";
    public string InboxFile { get; set; } = "Inbox.md";
}

public sealed class WebhookProjectBinding
{
    public string DisplayName { get; set; } = "Webhook";
}

public sealed class SpeechRecognitionSettings
{
    public string Model { get; set; } = "LargeV3Turbo";
    public string Language { get; set; } = "auto";
    public WhisperRuntimeKind Runtime { get; set; } = WhisperRuntimeKind.Cpu;
    public int ModelUnloadDelayMinutes { get; set; } = 5;
}

public enum WhisperRuntimeKind
{
    Cpu,
    Cuda,
}

public enum AiProviderKind
{
    Ollama,
    LmStudio,
    OpenAiCompatible,
}

public sealed class AiProviderSettings
{
    public AiProviderKind Kind { get; set; } = AiProviderKind.Ollama;
    public string BaseUrl { get; set; } = "http://127.0.0.1:11434";
    public string Model { get; set; } = "gemma4:12b";
}

public sealed class AudioSettings
{
    public string? MicrophoneDeviceId { get; set; }
    public string? PlaybackDeviceId { get; set; }
    public float MicrophoneGain { get; set; } = 1.0f;
    public float PlaybackGain { get; set; } = 0.65f;
}

public sealed class PlankaSettings
{
    public string BaseUrl { get; set; } = "http://192.168.1.100:1337";
    public string ListId { get; set; } = string.Empty;
    public string TargetDisplayName { get; set; } = string.Empty;
}

public sealed class GitHubSettings
{
    public string Repository { get; set; } = "Drakon7009/";
}

public sealed class ObsidianSettings
{
    public string VaultPath { get; set; } = string.Empty;
    public string Profile { get; set; } = "ProjectManager";
    public string ProjectFile { get; set; } = string.Empty;
    public string OutputFolder { get; set; } = "Speech2Issues";
    public string InboxFile { get; set; } = "Inbox.md";
    public string TaskNotesStatusField { get; set; } = "status";
    public string TaskNotesPriorityField { get; set; } = "priority";
    public string TaskNotesDueField { get; set; } = "due";
    public string TaskNotesStatus { get; set; } = "open";
    public string ProjectManagerStatus { get; set; } = "todo";
}

public sealed class WebhookSettings
{
    public string Url { get; set; } = string.Empty;
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class HotKeySettings
{
    public bool Ctrl { get; set; } = true;
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }
    public string Key { get; set; } = "F9";

    public bool IsValid => (Ctrl || Alt || Shift || Win) && !string.IsNullOrWhiteSpace(Key);

    public string Display
    {
        get
        {
            if (!IsValid)
            {
                return "Не задано";
            }

            var parts = new List<string>(5);
            if (Ctrl) parts.Add("Ctrl");
            if (Alt) parts.Add("Alt");
            if (Shift) parts.Add("Shift");
            if (Win) parts.Add("Win");
            parts.Add(FormatKey(Key));
            return string.Join(" + ", parts);
        }
    }

    private static string FormatKey(string key) => key switch
    {
        "D0" => "0",
        "D1" => "1",
        "D2" => "2",
        "D3" => "3",
        "D4" => "4",
        "D5" => "5",
        "D6" => "6",
        "D7" => "7",
        "D8" => "8",
        "D9" => "9",
        "OemPlus" => "=",
        "OemMinus" => "-",
        "OemComma" => ",",
        "OemPeriod" => ".",
        "OemQuestion" => "/",
        "OemSemicolon" => ";",
        "OemQuotes" => "'",
        "OemOpenBrackets" => "[",
        "OemCloseBrackets" => "]",
        "OemPipe" => "\\",
        _ => key,
    };
}

public sealed class AppSecrets
{
    public string AiApiToken { get; set; } = string.Empty;
    public string PlankaApiKey { get; set; } = string.Empty;
    public string GitHubToken { get; set; } = string.Empty;
    public Dictionary<string, string> WebhookHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

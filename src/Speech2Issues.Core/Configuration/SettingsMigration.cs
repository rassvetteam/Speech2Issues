using Speech2Issues.Core.Models;

namespace Speech2Issues.Core.Configuration;

public static class SettingsMigration
{
    public const int CurrentVersion = 4;

    public static bool Migrate(AppSettings settings)
    {
        var changed = settings.SchemaVersion < CurrentVersion;
        if (settings.SchemaVersion < 3)
        {
            settings.AiProvider = new AiProviderSettings
            {
                Kind = AiProviderKind.Ollama,
                BaseUrl = string.IsNullOrWhiteSpace(settings.LegacyOllamaUrl)
                    ? "http://127.0.0.1:11434"
                    : settings.LegacyOllamaUrl,
                Model = string.IsNullOrWhiteSpace(settings.LegacyOllamaModel)
                    ? "gemma4:12b"
                    : settings.LegacyOllamaModel,
            };
            settings.LegacyOllamaUrl = null;
            settings.LegacyOllamaModel = null;
        }
        if (settings.Projects.Count == 0 && !string.IsNullOrWhiteSpace(settings.Planka.ListId))
        {
            var display = settings.Planka.TargetDisplayName.Trim();
            var name = display.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var project = new ProjectProfile
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Мой проект" : name,
                Destinations =
                [
                    new DestinationBinding
                    {
                        Kind = DestinationKind.Planka,
                        DisplayName = string.IsNullOrWhiteSpace(display) ? "PLANKA" : display,
                        Planka = new PlankaProjectBinding
                        {
                            ProjectName = name ?? string.Empty,
                            FallbackListId = settings.Planka.ListId,
                            AllowedTargets =
                            [
                                new PlankaTargetBinding
                                {
                                    ListId = settings.Planka.ListId,
                                    DisplayName = string.IsNullOrWhiteSpace(display) ? settings.Planka.ListId : display,
                                },
                            ],
                        },
                    },
                ],
            };
            settings.Projects.Add(project);
            settings.LastActiveProjectId = project.Id;
            changed = true;
        }

        settings.SchemaVersion = CurrentVersion;
        return changed;
    }
}

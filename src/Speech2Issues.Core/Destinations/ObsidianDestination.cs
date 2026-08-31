using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Speech2Issues.Core.Configuration;
using Speech2Issues.Core.Models;
using Speech2Issues.Core.Storage;
using YamlDotNet.Serialization;

namespace Speech2Issues.Core.Destinations;

public sealed partial class ObsidianDestination(ObsidianSettings settings) : ITaskDestination
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();
    private static readonly ISerializer Serializer = new SerializerBuilder().DisableAliases().Build();

    public DestinationKind Kind => DestinationKind.Obsidian;

    public Task<ConnectionCheck> CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var vault = GetVaultPath();
            if (settings.Profile.Equals("ProjectManager", StringComparison.OrdinalIgnoreCase))
            {
                var projectFile = ResolveInVault(settings.ProjectFile);
                if (!File.Exists(projectFile))
                {
                    return Task.FromResult(new ConnectionCheck(false, "Выберите существующий project-файл Project Manager."));
                }
            }
            return Task.FromResult(new ConnectionCheck(true, $"Vault доступен: {vault}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ConnectionCheck(false, ex.Message));
        }
    }

    public async Task<IReadOnlyList<DestinationTarget>> LoadTargetsAsync(CancellationToken cancellationToken = default)
    {
        var vault = GetVaultPath();
        if (!settings.Profile.Equals("ProjectManager", StringComparison.OrdinalIgnoreCase))
        {
            var id = settings.OutputFolder;
            return [new(id, id)];
        }

        var targets = new List<DestinationTarget>();
        foreach (var file in Directory.EnumerateFiles(vault, "*.md", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string prefix;
            await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true))
            using (var reader = new StreamReader(stream))
            {
                var buffer = new char[4096];
                var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                prefix = new string(buffer, 0, count);
            }
            if (prefix.Contains("pm-project: true", StringComparison.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(vault, file);
                targets.Add(new(relative, Path.GetFileNameWithoutExtension(file), Path.GetDirectoryName(relative)));
            }
        }
        return targets.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task<CreatedTaskResult> CreateAsync(TaskDraft draft, CancellationToken cancellationToken = default)
    {
        _ = GetVaultPath();
        return settings.Profile.Trim().ToLowerInvariant() switch
        {
            "projectmanager" => await CreateProjectManagerAsync(draft, cancellationToken),
            "tasknotes" => await CreateTaskNotesAsync(draft, cancellationToken),
            "tasks" => await CreateTasksAsync(draft, cancellationToken),
            "markdown" => await CreateMarkdownAsync(draft, cancellationToken),
            _ => throw new InvalidOperationException("Неизвестный профиль Obsidian."),
        };
    }

    private async Task<CreatedTaskResult> CreateMarkdownAsync(TaskDraft draft, CancellationToken cancellationToken)
    {
        var folder = ResolveInVault(settings.OutputFolder);
        var desired = Path.Combine(folder, $"{Slug(draft.Title)}.md");
        var content = $"# {draft.Title}{Environment.NewLine}{Environment.NewLine}{TaskMarkdown.BuildBody(draft)}";
        var path = await FindExistingOrWriteAsync(folder, desired, draft, content, cancellationToken);
        return ResultFor(path, draft, path != desired);
    }

    private async Task<CreatedTaskResult> CreateTaskNotesAsync(TaskDraft draft, CancellationToken cancellationToken)
    {
        var folder = ResolveInVault(settings.OutputFolder);
        Directory.CreateDirectory(folder);
        var metadata = new Dictionary<object, object?>
        {
            ["title"] = draft.Title,
            [settings.TaskNotesStatusField] = settings.TaskNotesStatus,
            [settings.TaskNotesPriorityField] = draft.Priority == "critical" ? "high" : draft.Priority,
            [settings.TaskNotesDueField] = draft.DueDate,
            ["tags"] = draft.Labels.Prepend("task").Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ["dateCreated"] = draft.CreatedAt.ToString("O"),
            ["speech2issues-id"] = draft.Id,
        };
        var content = SerializeNote(metadata, TaskMarkdown.BuildBody(draft));
        var desired = Path.Combine(folder, $"{Slug(draft.Title)}.md");
        var path = await FindExistingOrWriteAsync(folder, desired, draft, content, cancellationToken);
        return ResultFor(path, draft, path != desired);
    }

    private async Task<CreatedTaskResult> CreateTasksAsync(TaskDraft draft, CancellationToken cancellationToken)
    {
        var folder = ResolveInVault(settings.OutputFolder);
        var desired = Path.Combine(folder, $"{Slug(draft.Title)}.md");
        var details = $"# {draft.Title}{Environment.NewLine}{Environment.NewLine}{TaskMarkdown.BuildBody(draft)}";
        var detailPath = await FindExistingOrWriteAsync(folder, desired, draft, details, cancellationToken);
        var inboxPath = ResolveInVault(settings.InboxFile);
        var relativeDetail = Path.ChangeExtension(Path.GetRelativePath(GetVaultPath(), detailPath), null).Replace('\\', '/');
        var priority = draft.Priority switch { "critical" => "🔺", "high" => "⏫", "low" => "🔽", _ => "🔼" };
        var due = draft.DueDate is null ? string.Empty : $" 📅 {draft.DueDate}";
        var line = $"- [ ] [[{relativeDetail}|{draft.Title}]] {priority}{due} <!-- speech2issues:{draft.Id} -->";
        await AppendUniqueLineAsync(inboxPath, line, draft.Id, cancellationToken);
        return ResultFor(detailPath, draft, false);
    }

    private async Task<CreatedTaskResult> CreateProjectManagerAsync(TaskDraft draft, CancellationToken cancellationToken)
    {
        var projectPath = ResolveInVault(settings.ProjectFile);
        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException("Project Manager project file not found.", projectPath);
        }

        var gate = FileLocks.GetOrAdd(projectPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        string? createdTaskPath = null;
        try
        {
            var projectWriteTime = File.GetLastWriteTimeUtc(projectPath);
            var projectText = await File.ReadAllTextAsync(projectPath, cancellationToken);
            if (File.GetLastWriteTimeUtc(projectPath) != projectWriteTime)
            {
                throw new IOException("Project file changed while it was being read. Retry the operation.");
            }
            var (projectMetadata, projectBody) = ParseNote(projectText);
            if (!GetBoolean(projectMetadata, "pm-project"))
            {
                throw new InvalidDataException("Selected file is not a Project Manager project.");
            }
            var projectId = GetString(projectMetadata, "id") ?? throw new InvalidDataException("Project Manager project id is missing.");
            var projectTitle = GetString(projectMetadata, "title") ?? Path.GetFileNameWithoutExtension(projectPath);

            var taskIds = GetStringList(projectMetadata, "taskIds");
            var taskId = $"t_{Guid.NewGuid():N}"[..14];
            var taskFolder = Path.Combine(Path.GetDirectoryName(projectPath)!, $"{projectTitle}_tasks");
            Directory.CreateDirectory(taskFolder);
            var desired = Path.Combine(taskFolder, $"{Slug(draft.Title)}.md");

            foreach (var existing in Directory.EnumerateFiles(taskFolder, "*.md"))
            {
                var text = await File.ReadAllTextAsync(existing, cancellationToken);
                if (text.Contains(TaskMarkdown.Marker(draft), StringComparison.Ordinal))
                {
                    return ResultFor(existing, draft, true);
                }
            }

            var metadata = new Dictionary<object, object?>
            {
                ["pm-task"] = true,
                ["projectId"] = projectId,
                ["parentId"] = null,
                ["id"] = taskId,
                ["title"] = draft.Title,
                ["type"] = "task",
                ["status"] = settings.ProjectManagerStatus,
                ["priority"] = draft.Priority == "medium" ? "medium" : draft.Priority,
                ["start"] = string.Empty,
                ["due"] = draft.DueDate ?? string.Empty,
                ["progress"] = 0,
                ["assignees"] = Array.Empty<string>(),
                ["tags"] = draft.Labels.ToArray(),
                ["subtaskIds"] = Array.Empty<string>(),
                ["dependencies"] = Array.Empty<string>(),
                ["collapsed"] = false,
                ["createdAt"] = draft.CreatedAt.ToString("O"),
                ["updatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                ["speech2issues-id"] = draft.Id,
            };
            createdTaskPath = await AtomicFile.WriteNewTextAsync(desired, SerializeNote(metadata, TaskMarkdown.BuildBody(draft)), cancellationToken);

            if (File.GetLastWriteTimeUtc(projectPath) != projectWriteTime)
            {
                throw new IOException("Project file changed while the task was being created. Retry the operation.");
            }
            taskIds.Add(taskId);
            projectMetadata["taskIds"] = taskIds;
            await AtomicFile.WriteTextAsync(projectPath, SerializeNote(projectMetadata, projectBody), cancellationToken);
            return ResultFor(createdTaskPath, draft, false);
        }
        catch
        {
            if (createdTaskPath is not null && File.Exists(createdTaskPath))
            {
                File.Delete(createdTaskPath);
            }
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> FindExistingOrWriteAsync(string folder, string desired, TaskDraft draft, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.EnumerateFiles(folder, "*.md"))
        {
            var text = await File.ReadAllTextAsync(file, cancellationToken);
            if (text.Contains(TaskMarkdown.Marker(draft), StringComparison.Ordinal) || text.Contains($"speech2issues-id: {draft.Id}", StringComparison.Ordinal))
            {
                return file;
            }
        }
        return await AtomicFile.WriteNewTextAsync(desired, content, cancellationToken);
    }

    private async Task AppendUniqueLineAsync(string path, string line, string id, CancellationToken cancellationToken)
    {
        var gate = FileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var writeTime = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                var content = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : string.Empty;
                if (File.Exists(path) && File.GetLastWriteTimeUtc(path) != writeTime)
                {
                    continue;
                }
                if (content.Contains($"speech2issues:{id}", StringComparison.Ordinal))
                {
                    return;
                }
                var updated = content.TrimEnd() + (string.IsNullOrWhiteSpace(content) ? string.Empty : Environment.NewLine) + line + Environment.NewLine;
                if (File.Exists(path) && File.GetLastWriteTimeUtc(path) != writeTime)
                {
                    continue;
                }
                await AtomicFile.WriteTextAsync(path, updated, cancellationToken);
                return;
            }
            throw new IOException("Inbox changed repeatedly while writing. Retry the operation.");
        }
        finally
        {
            gate.Release();
        }
    }

    private CreatedTaskResult ResultFor(string path, TaskDraft draft, bool existing)
    {
        var vault = GetVaultPath();
        var vaultName = Path.GetFileName(vault.TrimEnd(Path.DirectorySeparatorChar));
        var relative = Path.GetRelativePath(vault, path).Replace('\\', '/');
        var url = $"obsidian://open?vault={Uri.EscapeDataString(vaultName)}&file={Uri.EscapeDataString(relative)}";
        return new("Obsidian", draft.Id, url, existing);
    }

    private string GetVaultPath()
    {
        if (string.IsNullOrWhiteSpace(settings.VaultPath))
        {
            throw new InvalidOperationException("Укажите путь к Obsidian vault.");
        }
        var full = Path.GetFullPath(settings.VaultPath);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"Obsidian vault not found: {full}");
        }
        return full.TrimEnd(Path.DirectorySeparatorChar);
    }

    private string ResolveInVault(string relativeOrFull)
    {
        var vault = GetVaultPath();
        var full = Path.GetFullPath(Path.IsPathRooted(relativeOrFull) ? relativeOrFull : Path.Combine(vault, relativeOrFull));
        var relative = Path.GetRelativePath(vault, full);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Obsidian path must stay inside the configured vault.");
        }
        return full;
    }

    private static (Dictionary<object, object?> Metadata, string Body) ParseNote(string text)
    {
        if (!text.StartsWith("---", StringComparison.Ordinal))
        {
            return (new(), text);
        }
        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidDataException("Invalid YAML frontmatter.");
        }
        var yaml = text[3..end].Trim();
        var bodyStart = text.IndexOf('\n', end + 4);
        var body = bodyStart < 0 ? string.Empty : text[(bodyStart + 1)..];
        return (Deserializer.Deserialize<Dictionary<object, object?>>(yaml) ?? new(), body);
    }

    private static string SerializeNote(Dictionary<object, object?> metadata, string body) =>
        $"---{Environment.NewLine}{Serializer.Serialize(metadata).TrimEnd()}{Environment.NewLine}---{Environment.NewLine}{Environment.NewLine}{body.TrimStart()}";

    private static bool GetBoolean(Dictionary<object, object?> values, string key) =>
        values.TryGetValue(key, out var value) && (value is true || bool.TryParse(value?.ToString(), out var result) && result);

    private static string? GetString(Dictionary<object, object?> values, string key) =>
        values.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static List<string> GetStringList(Dictionary<object, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
        {
            return [];
        }
        if (value is IEnumerable<object> objects)
        {
            return objects.Select(x => x.ToString()).Where(x => x is not null).Cast<string>().ToList();
        }
        return [value.ToString()!];
    }

    private static string Slug(string value)
    {
        var slug = NonAlphaNumeric().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "task";
        }
        return slug.Length <= 40 ? slug : slug[..40].TrimEnd('-');
    }

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex NonAlphaNumeric();
}

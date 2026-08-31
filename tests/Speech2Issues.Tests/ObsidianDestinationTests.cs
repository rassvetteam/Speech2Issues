using Speech2Issues.Core.Configuration;
using Speech2Issues.Core.Destinations;
using Speech2Issues.Core.Models;

namespace Speech2Issues.Tests;

public sealed class ObsidianDestinationTests : IDisposable
{
    private readonly string _vault = Path.Combine(Path.GetTempPath(), $"speech2issues-tests-{Guid.NewGuid():N}");

    public ObsidianDestinationTests() => Directory.CreateDirectory(_vault);

    [Theory]
    [InlineData("Markdown")]
    [InlineData("TaskNotes")]
    public async Task CreatesStandaloneProfilesWithoutOverwrite(string profile)
    {
        var settings = new ObsidianSettings { VaultPath = _vault, Profile = profile, OutputFolder = profile };
        var destination = new ObsidianDestination(settings);
        var first = await destination.CreateAsync(Draft());
        var second = await destination.CreateAsync(Draft());
        var files = Directory.GetFiles(Path.Combine(_vault, profile), "*.md");

        Assert.Single(files);
        Assert.Equal(first.Url, second.Url);
        var content = await File.ReadAllTextAsync(files[0]);
        Assert.Contains("speech2issues", content);
        if (profile == "TaskNotes") Assert.Contains("tags:", content);
    }

    [Fact]
    public async Task TasksProfileCreatesDetailAndInboxCheckbox()
    {
        var settings = new ObsidianSettings
        {
            VaultPath = _vault,
            Profile = "Tasks",
            OutputFolder = "Details",
            InboxFile = "Inbox.md",
        };
        var destination = new ObsidianDestination(settings);
        await destination.CreateAsync(Draft());
        await destination.CreateAsync(Draft());

        var inbox = await File.ReadAllTextAsync(Path.Combine(_vault, "Inbox.md"));
        Assert.Single(inbox.Split('\n'), x => x.Contains("speech2issues:s2i-fixture"));
        Assert.Contains("- [ ] [[Details/", inbox);
    }

    [Fact]
    public async Task ProjectManagerCreatesFullTaskAndUpdatesTaskIdsPreservingUnknownField()
    {
        var project = Path.Combine(_vault, "Demo.md");
        await File.WriteAllTextAsync(project, """
            ---
            pm-project: true
            id: p_demo
            title: Demo
            description: keep
            taskIds: []
            unknownField: untouched
            ---

            Project body.
            """);
        var settings = new ObsidianSettings
        {
            VaultPath = _vault,
            Profile = "ProjectManager",
            ProjectFile = "Demo.md",
        };
        var destination = new ObsidianDestination(settings);

        await destination.CreateAsync(Draft());

        var task = Assert.Single(Directory.GetFiles(Path.Combine(_vault, "Demo_tasks"), "*.md"));
        var taskText = await File.ReadAllTextAsync(task);
        var projectText = await File.ReadAllTextAsync(project);
        Assert.Contains("pm-task: true", taskText);
        Assert.Contains("projectId: p_demo", taskText);
        Assert.Contains("unknownField: untouched", projectText);
        Assert.Contains("t_", projectText);
        Assert.Contains("Project body.", projectText);
    }

    private static TaskDraft Draft() => new()
    {
        Id = "s2i-fixture",
        Title = "Проверить интеграцию",
        Description = "Описание",
        Transcript = "Разговор",
        AcceptanceCriteria = ["Работает"],
        Labels = ["test"],
        Priority = "high",
        DueDate = "2026-09-01",
    };

    public void Dispose()
    {
        if (Directory.Exists(_vault))
        {
            Directory.Delete(_vault, true);
        }
    }
}

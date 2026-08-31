using Speech2Issues.Core.Models;

namespace Speech2Issues.Tests;

public sealed class TaskDraftTests
{
    [Fact]
    public void NormalizeConstrainsFieldsAndPreservesTranscript()
    {
        var draft = new TaskDraft
        {
            Title = "  Сделать задачу  ",
            Priority = "UNKNOWN",
            DueDate = "not-a-date",
            Labels = ["bug", "BUG", ""],
            AcceptanceCriteria = [" работает ", "работает"],
        }.Normalize(" исходная речь ");

        Assert.Equal("Сделать задачу", draft.Title);
        Assert.Equal("medium", draft.Priority);
        Assert.Null(draft.DueDate);
        Assert.Single(draft.Labels);
        Assert.Single(draft.AcceptanceCriteria);
        Assert.Equal("исходная речь", draft.Transcript);
    }
}

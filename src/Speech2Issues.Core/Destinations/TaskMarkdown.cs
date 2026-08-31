using System.Text;
using Speech2Issues.Core.Models;

namespace Speech2Issues.Core.Destinations;

public static class TaskMarkdown
{
    public static string Marker(TaskDraft draft) => $"<!-- speech2issues:{draft.Id} -->";

    public static string BuildBody(
        TaskDraft draft,
        bool includeTranscript = true,
        bool includeMarker = true,
        bool includeChecklist = true,
        bool includeMetadata = true)
    {
        var builder = new StringBuilder();
        if (includeMarker)
        {
            builder.AppendLine(Marker(draft));
            builder.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(draft.Description))
        {
            builder.AppendLine(draft.Description.Trim());
            builder.AppendLine();
        }
        if (includeChecklist && draft.AcceptanceCriteria.Count > 0)
        {
            builder.AppendLine("## Критерии готовности");
            foreach (var criterion in draft.AcceptanceCriteria)
            {
                builder.Append("- [ ] ").AppendLine(criterion);
            }
            builder.AppendLine();
        }
        if (includeMetadata)
        {
            builder.Append("**Приоритет:** ").AppendLine(draft.Priority);
            if (draft.DueDate is not null)
            {
                builder.Append("**Срок:** ").AppendLine(draft.DueDate);
            }
            if (draft.Labels.Count > 0)
            {
                builder.Append("**Метки:** ").AppendLine(string.Join(", ", draft.Labels));
            }
        }
        if (includeTranscript)
        {
            builder.AppendLine().AppendLine("## Исходная расшифровка").AppendLine(draft.Transcript);
        }
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }
}

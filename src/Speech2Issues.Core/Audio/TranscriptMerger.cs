using System.Text.RegularExpressions;

namespace Speech2Issues.Core.Audio;

public static partial class TranscriptMerger
{
    public static string Merge(IEnumerable<string> fragments)
    {
        var result = new List<string>();
        foreach (var fragment in fragments.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var words = Words().Matches(fragment.Trim()).Select(x => x.Value).ToArray();
            if (words.Length == 0)
            {
                continue;
            }

            var overlap = FindOverlap(result, words);
            result.AddRange(words.Skip(overlap));
        }
        return string.Join(' ', result).Trim();
    }

    private static int FindOverlap(IReadOnlyList<string> current, IReadOnlyList<string> next)
    {
        var max = Math.Min(Math.Min(current.Count, next.Count), 24);
        for (var count = max; count >= 2; count--)
        {
            var matches = true;
            for (var i = 0; i < count; i++)
            {
                if (!string.Equals(Normalize(current[current.Count - count + i]), Normalize(next[i]), StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            }
            if (matches)
            {
                return count;
            }
        }
        return 0;
    }

    private static string Normalize(string word) => word.Trim(' ', '.', ',', '!', '?', ':', ';', '"', '\'', '(', ')').ToLowerInvariant();

    [GeneratedRegex(@"\S+")]
    private static partial Regex Words();
}

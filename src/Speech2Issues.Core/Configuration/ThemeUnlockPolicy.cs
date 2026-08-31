namespace Speech2Issues.Core.Configuration;

public static class ThemeUnlockPolicy
{
    public const int AstolfoRequiredCreatedTasks = 100;

    public static bool IsAstolfoUnlocked(int createdTaskCount) =>
        createdTaskCount >= AstolfoRequiredCreatedTasks;
}

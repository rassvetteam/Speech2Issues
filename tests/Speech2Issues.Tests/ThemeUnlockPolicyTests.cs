using Speech2Issues.Core.Configuration;

namespace Speech2Issues.Tests;

public sealed class ThemeUnlockPolicyTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(101, true)]
    public void AstolfoUnlocksExactlyAtOneHundredCreatedTasks(int createdTaskCount, bool expected)
    {
        Assert.Equal(expected, ThemeUnlockPolicy.IsAstolfoUnlocked(createdTaskCount));
    }
}

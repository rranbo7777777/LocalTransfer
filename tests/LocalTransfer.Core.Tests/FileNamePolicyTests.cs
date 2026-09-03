using LocalTransfer.Core.Transfers;

namespace LocalTransfer.Core.Tests;

public sealed class FileNamePolicyTests
{
    [Theory]
    [InlineData("report?.pdf", "report_.pdf")]
    [InlineData("a<b>c.txt", "a_b_c.txt")]
    [InlineData("trailing. ", "trailing")]
    public void Sanitize_ReplacesWindowsInvalidCharacters(string input, string expected)
    {
        Assert.Equal(expected, FileNamePolicy.Sanitize(input));
    }

    [Fact]
    public void Sanitize_TruncatesStemAndPreservesExtension()
    {
        var result = FileNamePolicy.Sanitize($"{new string('a', 100)}.zip", 32);

        Assert.Equal(32, result.Length);
        Assert.EndsWith(".zip", result, StringComparison.Ordinal);
    }
}

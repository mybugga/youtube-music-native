using YouTubeMusicNative.Services;
using Xunit;

namespace YouTubeMusicNative.Tests;

public class UpdateTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("V2.0.1-beta", "2.0.1")]
    [InlineData("1.0.0+abc123", "1.0.0")]
    public void ParsesReleaseTags(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateService.ParseVersion(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    public void RejectsNonVersions(string? tag) => Assert.Null(UpdateService.ParseVersion(tag));

    [Fact]
    public void NewerTagCompares() =>
        Assert.True(UpdateService.ParseVersion("v1.0.10") > UpdateService.ParseVersion("v1.0.9"));
}

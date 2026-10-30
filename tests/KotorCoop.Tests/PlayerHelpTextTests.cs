using KotorCoop.Agent;
using Xunit;

namespace KotorCoop.Tests;

public class PlayerHelpTextTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(32)]
    public void HelpDirectionsSurviveTheOverlayWidthWithoutLosingWords(int width)
    {
        const string text = "The host has stopped responding. Press F9 and choose Retry last connection or Leave co-op.";
        string[] lines = PlayerHelpText.Wrap(text, width).ToArray();
        Assert.All(lines, line => Assert.InRange(line.Length, 1, width));
        Assert.Equal(text, string.Join(' ', lines));
    }

    [Fact]
    public void ReportLocationAndCompleteFilenameAreSeparateVisibleLines()
    {
        const string name = "kotorcoop-support-20261030-120000-12345678.zip";
        string[] lines = PlayerHelpText.Wrap("Support report saved on your Desktop:\r\n" + name).ToArray();
        Assert.Equal(new[] { "Support report saved on your Desktop:", name }, lines);
        Assert.All(lines, line => Assert.InRange(line.Length, 1, PlayerHelpText.DefaultWidth));
    }

    [Fact]
    public void ALongUnbrokenValueIsPreservedRatherThanTruncated()
    {
        string text = new('x', 137);
        string[] lines = PlayerHelpText.Wrap(text).ToArray();
        Assert.All(lines, line => Assert.InRange(line.Length, 1, PlayerHelpText.DefaultWidth));
        Assert.Equal(text, string.Concat(lines));
    }

    [Fact]
    public void EmptyParagraphsProduceNoBlankOverlayMessages()
    {
        Assert.Empty(PlayerHelpText.Wrap(" \r\n \n"));
    }
}

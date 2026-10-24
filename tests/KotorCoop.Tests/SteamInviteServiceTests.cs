using KotorCoop.Steam;
using Xunit;

namespace KotorCoop.Tests;

public sealed class SteamInviteServiceTests
{
    [Fact]
    public void ExistingForeignAppIdIsPreservedAndActivationIsRefused()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"kcoop-steam-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, SteamInviteService.AppIdFileName);
            File.WriteAllText(path, "480\r\n");
            List<string> logs = [];
            Assert.False(SteamInviteService.TryEnsureAppIdFile(directory, 32370, logs.Add));
            Assert.Equal("480\r\n", File.ReadAllText(path));
            Assert.Contains(logs, line => line.Contains("left unchanged"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void CorrectExistingAppIdIsNotRewritten()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"kcoop-steam-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, SteamInviteService.AppIdFileName);
            File.WriteAllText(path, "32370\r\n");
            List<string> logs = [];
            Assert.True(SteamInviteService.TryEnsureAppIdFile(directory, 32370, logs.Add));
            Assert.Equal("32370\r\n", File.ReadAllText(path));
            Assert.Empty(logs);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void MissingAppIdIsCreatedForTheRequestedApplication()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"kcoop-steam-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            Assert.True(SteamInviteService.TryEnsureAppIdFile(directory, 32370, _ => { }));
            Assert.Equal("32370", File.ReadAllText(Path.Combine(directory, SteamInviteService.AppIdFileName)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void DisabledServiceNeverPublishesOrLoadsSteam()
    {
        List<string> logs = [];
        using SteamInviteService service = new(false, logs.Add);
        service.Publish("kc1/127.0.0.1:27140/c2VjcmV0");
        Assert.False(service.TryActivate());
        Assert.False(service.IsActive);
        Assert.False(service.Invite(1, "anything"));
        service.Pump(_ => Assert.Fail("Disabled Steam must not dispatch callbacks"));
        Assert.Empty(logs);
    }
}

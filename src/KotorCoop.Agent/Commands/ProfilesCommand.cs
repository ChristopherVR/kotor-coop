using KotorCoop.Memory;

namespace KotorCoop.Agent;

/// <summary>The `agent profiles` verb: lists the JSON offset profiles this build can load.</summary>
internal static class ProfilesCommand
{
    public static int Run()
    {
        IReadOnlyList<string> profiles = ProfileLocator.ListAvailable();

        if (profiles.Count == 0)
        {
            Console.WriteLine("No profiles found.");
            return 1;
        }

        Console.WriteLine("Available profiles:");

        foreach (string name in profiles)
        {
            string status;
            try
            {
                GameProfile profile = GameProfile.Load(ProfileLocator.Resolve(name));
                status = profile.Remotes is null ? "loads (read-only)" : "loads (read/write)";
            }
            catch (Exception ex)
            {
                // Splitting on '.' would cut the message at the file path, not the
                // sentence, so trim by length instead.
                string message = ex.Message.ReplaceLineEndings(" ");
                status = "incomplete: " + (message.Length > 90 ? message[..90] + "..." : message);
            }

            Console.WriteLine($"  {name,-12} {status}");
        }

        return 0;
    }
}

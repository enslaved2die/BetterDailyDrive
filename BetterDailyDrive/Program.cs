using System;
using System.Threading.Tasks;
using SpotifyAPI.Web; // Only needed for ISpotifyClient type

public class SpotifyCli
{
    /// <summary>
    /// 1. Initialize AuthManager
    /// 2. Get Authenticated Client
    /// 3. Initialize PlaylistManager
    /// 4. Run the main feature
    /// </summary>
    public static async Task Main(string[] args)
    {
        var cli = new SpotifyCli();
        await cli.RunAsync();
    }

    public async Task RunAsync()
    {
        Console.WriteLine("--- Spotify CLI Initializing ---");

        // Initialize Authentication Manager
        var authManager = new AuthManager();

        // Get the authenticated client (handles token load, refresh, or new flow)
        Console.WriteLine("--- Starting Authentication Process ---");
        ISpotifyClient? spotifyClient = await authManager.GetAuthenticatedClientAsync();

        if (spotifyClient != null)
        {
            Console.WriteLine("\n--- Authentication Successful. Starting Playlist Feature ---");

            // Initialize Playlist Manager with the authenticated client
            var playlistManager = new PlaylistManager(spotifyClient);
            
            // Start the main feature requested by the user
            await playlistManager.AddPodcastsToDailyDriveAsync();
        }
        else
        {
            Console.WriteLine("Authentication failed. Cannot proceed with API calls.");
        }
    }
}
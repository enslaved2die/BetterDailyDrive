// Manages the application-specific logic: list shows, playlist creation, track consolidation, and interleaving.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SpotifyAPI.Web;

public class PlaylistManager
{
    private const string TargetPlaylistName = "Better Daily Drive";
    private const int MaxTracksToSelect = 50; // Fixed amount of music tracks
    private const int MusicInterleaveInterval = 5; // Podcast inserted after every 5 songs
    private const string ConfigFileName = "playlist_config.json";

    private readonly ISpotifyClient _spotifyClient;

    // --- Configuration Structure ---
    public class PlaylistConfiguration
    {
        // Stores only the IDs, as FullPlaylist/FullShow are too large and complex to save
        public List<string> SourcePlaylistIds { get; set; } = new List<string>();
        public List<string> ShowIds { get; set; } = new List<string>();
    }

    public PlaylistManager(ISpotifyClient spotifyClient)
    {
        _spotifyClient = spotifyClient ?? throw new ArgumentNullException(nameof(spotifyClient));
    }

    /// <summary>
    /// Executes the main logic: determines whether to run interactive setup or use a saved configuration, 
    /// then builds and updates the target playlist.
    /// </summary>
    public async Task AddPodcastsToDailyDriveAsync()
    {
        // 1. Get User ID (required for creating the playlist)
        var profile = await _spotifyClient.UserProfile.Current();
        string userId = profile.Id;
        Console.WriteLine($"\nSuccess! Welcome, {profile.DisplayName ?? profile.Id}! (User ID: {userId})");

        // --- Step 1: Destination Playlist Setup ---
        Console.WriteLine($"\n--- Step 1: Destination Playlist Setup ---");
        var destinationPlaylistId = await FindOrCreatePlaylistAsync(userId, TargetPlaylistName);

        if (destinationPlaylistId == null)
        {
            Console.WriteLine("FATAL: Could not find or create the target playlist. Aborting.");
            return;
        }

        // --- Step 2 & 3: Load or Create Configuration ---
        PlaylistConfiguration? config = await LoadConfigurationAsync();
        bool runInteractiveSetup = false;
        
        if (config != null)
        {
            // Config found. Wait for 10s or 'S' press.
            runInteractiveSetup = await ShouldRunSetupOrWaitAsync(10);
        }
        else
        {
            // No config found, must run interactive setup.
            Console.WriteLine("No saved configuration found. Starting interactive setup.");
            runInteractiveSetup = true;
        }

        if (runInteractiveSetup)
        {
            // INTERACTIVE SETUP BLOCK
            
            // Select Source Playlists (Music)
            Console.WriteLine($"\n--- Step 2: Select Source Playlists (Music) ---");
            var selectedPlaylists = await SelectSourcePlaylistsAsync(destinationPlaylistId);

            if (selectedPlaylists == null || !selectedPlaylists.Any())
            {
                 Console.WriteLine("No source playlists selected. Aborting.");
                 return;
            }

            // Select Podcasts for Interleaving
            Console.WriteLine($"\n--- Step 3: Select Podcasts for Interleaving ---");
            var selectedShows = await SelectPodcastsAsync();

            config = new PlaylistConfiguration
            {
                SourcePlaylistIds = selectedPlaylists.Select(p => p.Id!).Where(id => !string.IsNullOrEmpty(id)).ToList(),
                ShowIds = selectedShows?.Select(s => s.Id).Where(id => !string.IsNullOrEmpty(id)).ToList() ?? new List<string>()
            };
            
            await SaveConfigurationAsync(config);
        }

        // Configuration is guaranteed here (either loaded or created)
        if (config == null)
        {
            Console.WriteLine("FATAL: Configuration is null after setup attempt. Aborting.");
            return;
        }

        // --- Step 4: Load Full Objects from Configured IDs ---
        var finalSourcePlaylists = await LoadSourcePlaylistsAsync(config.SourcePlaylistIds);
        var finalSelectedShows = await LoadSelectedShowsAsync(config.ShowIds);

        Console.WriteLine($"\nSelected {finalSourcePlaylists.Count} music source playlist(s) and {finalSelectedShows.Count} show(s).");

        // --- Step 5: Curate, Interleave, and Update Playlist ---
        Console.WriteLine($"\n--- Step 5: Curating and Copying tracks to '{TargetPlaylistName}' ---");
        await ConsolidateAndInterleaveTracksAsync(destinationPlaylistId, finalSourcePlaylists, finalSelectedShows);

        Console.WriteLine("\n--- Operation Complete ---");
        Console.WriteLine($"Successfully consolidated and curated the final playlist '{TargetPlaylistName}'.");
    }
    
    /// <summary>
    /// Waits for a specified duration, checking for user input to abort the auto-run.
    /// FIX: Corrected delay loop to ensure the timeout is accurate (1 second per count).
    /// </summary>
    /// <returns>True if setup should run, False to use the saved config.</returns>
    private async Task<bool> ShouldRunSetupOrWaitAsync(int timeoutSeconds)
    {
        Console.WriteLine("\n--- Auto-Run Check ---");
        Console.WriteLine($"A saved configuration was found. Auto-running in {timeoutSeconds} seconds (default action).");
        Console.WriteLine("Press 'S' or 's' now to abort and start interactive Setup.");
        
        // This loop iterates once per second
        for (int i = timeoutSeconds; i > 0; i--)
        {
            // Overwrite the countdown number using carriage return \r
            Console.Write($"\rCountdown: {i}  "); 

            // Wait up to 1 second, checking for input frequently (10 times per second)
            const int checkIntervalMs = 100;
            const int checksPerSecond = 1000 / checkIntervalMs;
            
            for (int j = 0; j < checksPerSecond; j++) 
            {
                if (Console.KeyAvailable)
                {
                    var keyInfo = Console.ReadKey(true); // Read key without echoing
                    
                    if (keyInfo.KeyChar.ToString().Equals("S", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("\nSetup request detected. Starting interactive setup...");
                        return true; // Run setup
                    }
                }
                await Task.Delay(checkIntervalMs); // Wait 100ms
            }
        }
        
        Console.WriteLine("\nTimeout reached. Proceeding with saved configuration.");
        return false; // Run with config
    }

    // --- Persistence Methods ---

    private async Task SaveConfigurationAsync(PlaylistConfiguration config)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string jsonString = JsonSerializer.Serialize(config, options);
            await File.WriteAllTextAsync(ConfigFileName, jsonString);
            Console.WriteLine($"\nConfiguration saved to {ConfigFileName}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving configuration: {ex.Message}");
        }
    }

    private async Task<PlaylistConfiguration?> LoadConfigurationAsync()
    {
        if (!File.Exists(ConfigFileName))
        {
            return null;
        }
        try
        {
            string jsonString = await File.ReadAllTextAsync(ConfigFileName);
            var config = JsonSerializer.Deserialize<PlaylistConfiguration>(jsonString);
            
            if (config == null || !config.SourcePlaylistIds.Any())
            {
                Console.WriteLine($"Error: Configuration file '{ConfigFileName}' found, but is empty or corrupt.");
                File.Delete(ConfigFileName);
                return null;
            }

            Console.WriteLine($"Configuration loaded from {ConfigFileName}.");
            return config;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading configuration: {ex.Message}");
            // Delete potentially corrupt file to force re-setup next time
            File.Delete(ConfigFileName); 
            return null;
        }
    }
    
    // --- Loading Full Objects from IDs ---

    private async Task<List<FullPlaylist>> LoadSourcePlaylistsAsync(List<string> playlistIds)
    {
        var playlists = new List<FullPlaylist>();
        foreach (var id in playlistIds)
        {
            try
            {
                var playlist = await _spotifyClient.Playlists.Get(id);
                if (playlist != null) playlists.Add(playlist);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"- Warning: Could not load source playlist ID '{id}'. It may have been deleted. ({ex.Message})");
            }
        }
        return playlists;
    }

    private async Task<List<FullShow>> LoadSelectedShowsAsync(List<string> showIds)
    {
        var shows = new List<FullShow>();
        foreach (var id in showIds)
        {
            try
            {
                // Spotify API allows batch fetching of shows, but using individual GETs for simplicity here
                var show = await _spotifyClient.Shows.Get(id);
                if (show != null) shows.Add(show);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"- Warning: Could not load selected show ID '{id}'. It may have been unfollowed. ({ex.Message})");
            }
        }
        return shows;
    }


    /// <summary>
    /// Finds a playlist by name for the user, or creates it if it doesn't exist.
    /// </summary>
    private async Task<string?> FindOrCreatePlaylistAsync(string userId, string playlistName)
    {
        Console.WriteLine($"Searching for destination playlist: '{playlistName}'...");
        var paging = await _spotifyClient.Playlists.CurrentUsers();
        
        if (paging == null)
        {
            Console.WriteLine("Error: Failed to fetch user playlists from Spotify API.");
            return null;
        }
        
        // PaginateAll returns List<SimplePlaylist>, which is then treated as a generic list.
        var playlists = await _spotifyClient.PaginateAll(paging);

        // Added '!' to p.Name to resolve Warning CS8602 regarding possible null reference.
        var existingPlaylist = playlists.FirstOrDefault(p => p.Name!.Equals(playlistName, StringComparison.OrdinalIgnoreCase));

        if (existingPlaylist != null)
        {
            Console.WriteLine($"Found existing playlist: {existingPlaylist.Name}");
            return existingPlaylist.Id;
        }

        try
        {
            var newPlaylist = await _spotifyClient.Playlists.Create(userId, new PlaylistCreateRequest(playlistName)
            {
                Public = false,
                Description = $"Curated playlist of {MaxTracksToSelect} tracks interleaved with podcasts. Created by Spotify CLI."
            });
            Console.WriteLine($"Created new playlist: {newPlaylist.Name}");
            return newPlaylist.Id;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to create playlist: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Prompts the user to select one or more existing playlists to use as track sources.
    /// </summary>
    private async Task<List<FullPlaylist>?> SelectSourcePlaylistsAsync(string destinationId)
    {
        var paging = await _spotifyClient.Playlists.CurrentUsers();
        if (paging == null)
        {
            Console.WriteLine("Error: Failed to fetch user playlists from Spotify API.");
            return null;
        }
        
        // PaginateAll returns a list of SimplePlaylist, which we cast to FullPlaylist to maintain type consistency
        // due to SimplePlaylist being unavailable in the current context.
        var allPlaylists = await _spotifyClient.PaginateAll(paging);
        
        if (!allPlaylists.Any())
        {
            Console.WriteLine("No playlists found to use as a source. Aborting.");
            return null;
        }
        
        // Filter out the destination playlist itself
        var sourceCandidates = allPlaylists
            .Where(p => p.Id != destinationId)
            .Cast<FullPlaylist>() // Cast required to resolve compilation issues
            .ToList();

        if (!sourceCandidates.Any())
        {
            Console.WriteLine("No other playlists available to use as a source.");
            return null;
        }

        // Display list of existing playlists
        Console.WriteLine($"Found {sourceCandidates.Count} potential source playlists. Select the numbers (e.g., 1,3,5) of the playlists you want to consolidate tracks from:");

        for (int i = 0; i < sourceCandidates.Count; i++)
        {
            var sourcePlaylist = sourceCandidates[i]; 
            // Note: FullPlaylist exposes Tracks.Total
            Console.WriteLine($"  {i + 1}. {sourcePlaylist.Name} (Tracks: {sourcePlaylist.Tracks?.Total})");
        }
        
        Console.Write("\nEnter playlist numbers, separated by commas: ");
        var input = Console.ReadLine() ?? string.Empty;

        var selectedIndices = input.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                   .Where(s => int.TryParse(s, out _))
                                   .Select(int.Parse)
                                   .Distinct()
                                   .ToList();

        var selectedPlaylists = new List<FullPlaylist>(); 
        foreach (var index in selectedIndices)
        {
            if (index > 0 && index <= sourceCandidates.Count)
            {
                selectedPlaylists.Add(sourceCandidates[index - 1]);
            }
        }
        
        return selectedPlaylists;
    }

    /// <summary>
    /// Fetches all followed shows for the current user.
    /// </summary>
    private async Task<List<FullShow>> ListFollowedShowsAsync()
    {
        var shows = new List<FullShow>();
        var request = new LibraryShowsRequest
        {
            Limit = 50 
        };
        
        var response = await _spotifyClient.Library.GetShows(request);
        
        if (response == null) return shows;

        var allSavedShows = await _spotifyClient.PaginateAll(response);

        shows.AddRange(allSavedShows.Select(s => s.Show).Where(s => s != null).Cast<FullShow>());
        
        return shows.OrderBy(s => s.Name).ToList();
    }
    
    /// <summary>
    /// Prompts the user to select one or more followed podcasts (Spotify Shows).
    /// </summary>
    private async Task<List<FullShow>?> SelectPodcastsAsync()
    {
        var shows = await ListFollowedShowsAsync();
        
        if (shows == null || !shows.Any())
        {
            Console.WriteLine("You are not following any shows. Cannot interleave podcasts.");
            return null;
        }
        
        // Display list of shows
        Console.WriteLine($"\nFound {shows.Count} followed shows. Enter the numbers (1-{shows.Count}) of the shows you want to include, separated by commas (e.g., 1,3,5):");
        for (int i = 0; i < shows.Count; i++)
        {
            Console.WriteLine($"  {i + 1}. {shows[i].Name} ({shows[i].Publisher})");
        }
        
        // Read input and parse selection
        var input = Console.ReadLine() ?? string.Empty;
        var selectedIndices = input.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                   .Where(s => int.TryParse(s, out _))
                                   .Select(int.Parse)
                                   .Distinct()
                                   .ToList();

        var selectedShows = new List<FullShow>(); 
        foreach (var index in selectedIndices)
        {
            if (index > 0 && index <= shows.Count)
            {
                selectedShows.Add(shows[index - 1]);
            }
        }
        
        return selectedShows;
    }

    /// <summary>
    /// Consolidates tracks from source playlists (randomly selected and shuffled), 
    /// interleaves them with podcast episodes, clears the destination, and adds new tracks.
    /// </summary>
    private async Task ConsolidateAndInterleaveTracksAsync(string destinationId, List<FullPlaylist> sourcePlaylists, List<FullShow> selectedShows)
    {
        var allTrackUris = new List<string>();
        
        Console.WriteLine("Fetching all track URIs from source playlists...");
        
        // 1. CONSOLIDATE MUSIC TRACKS (Collect ALL available tracks from all sources)
        foreach (var source in sourcePlaylists)
        {
            // The null-forgiving operator '!' is added to source.Id to resolve Warning CS8604.
            var fullPlaylist = await _spotifyClient.Playlists.Get(source.Id!); 
            if (fullPlaylist?.Tracks?.Items == null) continue;

            // Paginate all tracks from this source playlist
            var allTracks = await _spotifyClient.PaginateAll(fullPlaylist.Tracks);

            // FIX: Correctly check for FullTrack and extract the URI.
            var urisFromSource = allTracks
                // Select only tracks that are music (FullTrack)
                .Select(t => t.Track)
                .Where(track => track is FullTrack)
                .Cast<FullTrack>()
                .Select(track => track.Uri)
                .Where(uri => !string.IsNullOrEmpty(uri))
                .ToList();
            
            allTrackUris.AddRange(urisFromSource);
            Console.WriteLine($"- Collected {urisFromSource.Count} tracks from '{source.Name}'. Total collected: {allTrackUris.Count}");
        }

        if (!allTrackUris.Any())
        {
            Console.WriteLine("No music tracks were found. Aborting copy.");
            return;
        }
        
        // 2. RANDOMLY SELECT & SHUFFLE MUSIC TRACKS
        var random = new Random();
        
        // Randomly select up to MaxTracksToSelect (50) from the entire pool, 
        // and shuffle the result (OrderBy(x => random.Next()) handles both steps).
        var shuffledTracks = allTrackUris
            .OrderBy(x => random.Next())
            .Take(MaxTracksToSelect)
            .ToList();
            
        Console.WriteLine($"Randomly selected and shuffled {shuffledTracks.Count} tracks for final playlist.");
        
        // 3. GET PODCAST EPISODES (URIs)
        var podcastUris = await GetLatestEpisodeUrisAsync(selectedShows);
        var finalUris = new List<string>();
        
        if (!podcastUris.Any() || shuffledTracks.Count == 0)
        {
            // If no podcasts or no music, use the music list as is (if available)
            Console.WriteLine("Cannot interleave. Using music tracks only.");
            finalUris = shuffledTracks;
        }
        else
        {
            // 4. INTERLEAVE MUSIC AND PODCASTS using the custom pattern

            int podcastIndex = 0;
            
            // Pattern start: Track 1, then Podcast 1
            if (shuffledTracks.Count > 0)
            {
                // Add the first track
                finalUris.Add(shuffledTracks[0]);

                // Check if a unique podcast is available before adding the first one.
                if (podcastIndex < podcastUris.Count)
                {
                    finalUris.Add(podcastUris[podcastIndex]);
                    podcastIndex++;
                }
            }
            
            // Loop through the remaining music tracks (starting at index 1)
            for (int i = 1; i < shuffledTracks.Count; i++)
            {
                // Add the current music track
                finalUris.Add(shuffledTracks[i]);

                // The normal sequence starts AFTER the first two items (Track, Podcast).
                // We check if (i) is an index where a break should occur (after every 5 songs).
                // Check if we have run out of unique podcast URIs before inserting.
                if (podcastIndex < podcastUris.Count && (i - 1) > 0 && (i - 1) % MusicInterleaveInterval == 0)
                {
                    // Insert the next unique podcast episode
                    finalUris.Add(podcastUris[podcastIndex]);
                    podcastIndex++;
                }
            }
            Console.WriteLine($"Interleaved {podcastIndex} unique podcast episodes with {shuffledTracks.Count} songs.");
        }
        
        // 5. CLEAR AND ADD TO PLAYLIST
        
        // Clear the destination playlist before adding new tracks
        Console.WriteLine($"\nClearing existing content from destination playlist...");
        await ClearPlaylistAsync(destinationId);
        
        // Add consolidated track URIs in chunks (Spotify limits add to 100 items per request)
        Console.WriteLine($"Adding {finalUris.Count} items to destination playlist...");
        int addedCount = 0;

        // Ensure we use the built-in Chunk method for iteration, which requires System.Linq.
        foreach (var chunk in finalUris.Chunk(100))
        {
            var request = new PlaylistAddItemsRequest(chunk.ToList());
            await _spotifyClient.Playlists.AddItems(destinationId, request);
            addedCount += chunk.Length;
        }

        Console.WriteLine($"Successfully updated playlist with {addedCount} items.");
    }
    
    /// <summary>
    /// Fetches the latest episode URI for each selected show.
    /// </summary>
    private async Task<List<string>> GetLatestEpisodeUrisAsync(List<FullShow> selectedShows)
    {
        var episodeUris = new List<string>();
        
        foreach (var show in selectedShows)
        {
            try
            {
                // Fetch only the single latest episode
                var episodePage = await _spotifyClient.Shows.GetEpisodes(show.Id, new ShowEpisodesRequest { Limit = 1 });
                
                var latestEpisode = episodePage?.Items?.FirstOrDefault(); 

                if (latestEpisode != null && !string.IsNullOrEmpty(latestEpisode.Uri))
                {
                    episodeUris.Add(latestEpisode.Uri);
                    Console.WriteLine($"- Found latest episode from: {show.Name}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"- Error fetching episode for {show.Name}: {ex.Message}");
            }
        }
        
        return episodeUris;
    }

    /// <summary>
    /// Clears all tracks from a playlist.
    /// </summary>
    private async Task ClearPlaylistAsync(string playlistId)
    {
        var fullPlaylist = await _spotifyClient.Playlists.Get(playlistId);
        
        if (fullPlaylist?.Tracks?.Items == null) return;
        
        // Paginate all current tracks to find their URIs
        var allCurrentTracks = await _spotifyClient.PaginateAll(fullPlaylist.Tracks);
        
        // Safely access the URI regardless of whether the item is a FullTrack or FullEpisode.
        var tracksToDelete = allCurrentTracks
            .Select(t => t.Track)
            .Where(track => track != null)
            .Select(track =>
            {
                // Use pattern matching to safely get the Uri from either FullTrack or FullEpisode
                if (track is FullTrack fullTrack) return fullTrack.Uri;
                if (track is FullEpisode fullEpisode) return fullEpisode.Uri;
                return null;
            })
            .Where(uri => !string.IsNullOrEmpty(uri))
            .Select(uri => new PlaylistRemoveItemsRequest.Item { Uri = uri! })
            .ToList();

        if (!tracksToDelete.Any())
        {
            Console.WriteLine("Destination playlist was already empty.");
            return;
        }

        // Delete items in chunks (Spotify limits remove to 100 items per request)
        foreach (var chunk in tracksToDelete.Chunk(100))
        {
            var request = new PlaylistRemoveItemsRequest { Tracks = chunk.ToList() };
            await _spotifyClient.Playlists.RemoveItems(playlistId, request);
        }
        Console.WriteLine($"Removed {tracksToDelete.Count} old items.");
    }
}

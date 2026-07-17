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
        // Once resolved, the destination playlist is pinned by ID here and never re-guessed by name -
        // see EnsureDestinationPlaylistAsync. Nullable/omitted on older config files and filled in on
        // first use after upgrading.
        public string? DestinationPlaylistId { get; set; }

        // Stores only the IDs, as FullPlaylist/FullShow are too large and complex to save.
        // ShowIds is order-significant: it's the sequence podcasts get interleaved in.
        public List<string> SourcePlaylistIds { get; set; } = new List<string>();
        public List<string> ShowIds { get; set; } = new List<string>();
    }

    // A display-friendly row for the destination playlist's current contents (used by the web UI).
    public class TrackSummary
    {
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string? ImageUrl { get; set; }
        public bool IsEpisode { get; set; }
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
        var userId = await GetCurrentUserIdAsync();
        Console.WriteLine($"\nSuccess! (User ID: {userId})");

        // --- Step 1: Destination Playlist Setup ---
        Console.WriteLine($"\n--- Step 1: Destination Playlist Setup ---");
        var destinationPlaylistId = await EnsureDestinationPlaylistAsync(userId);

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
            // This method is now guarded against non-interactive environments (cron).
            runInteractiveSetup = await ShouldRunSetupOrWaitAsync(10);
        }
        else
        {
            // No config found, must run interactive setup.
            // NOTE: If running non-interactively without a config, this will proceed to Console.ReadLine()
            // and likely fail the setup unless input is provided via redirection.
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

        await RunPipelineAsync(destinationPlaylistId, config);
    }

    /// <summary>
    /// Gets the current user's Spotify ID.
    /// </summary>
    public async Task<string> GetCurrentUserIdAsync()
    {
        var profile = await _spotifyClient.UserProfile.Current();
        return profile.Id;
    }

    /// <summary>
    /// Finds or creates the app's target playlist for the given user. Once resolved, the playlist ID is
    /// pinned in the saved configuration and reused directly on every later call - it is never re-guessed
    /// by name again. This matters because a name-based search is fundamentally unsafe to repeat: if the
    /// user ever ends up with two playlists sharing the exact name (e.g. one was created by an earlier,
    /// broken run of this app before this fix existed), re-searching by name on every run risks silently
    /// picking a different one each time, updating the wrong playlist while the "real" one never changes.
    /// </summary>
    public async Task<string?> EnsureDestinationPlaylistAsync(string userId)
    {
        var config = await LoadConfigurationAsync() ?? new PlaylistConfiguration();

        if (!string.IsNullOrEmpty(config.DestinationPlaylistId))
        {
            try
            {
                var pinned = await _spotifyClient.Playlists.Get(config.DestinationPlaylistId);
                if (pinned != null)
                {
                    Console.WriteLine($"Using pinned destination playlist: {pinned.Name} ({pinned.Id})");
                    await TrySetCoverImageAsync(pinned.Id!);
                    return pinned.Id;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Pinned destination playlist ID '{config.DestinationPlaylistId}' is no longer valid " +
                    $"({ex.Message}). Falling back to a name search and re-pinning whatever is found/created.");
            }
        }

        var resolvedId = await FindOrCreatePlaylistAsync(userId, TargetPlaylistName);
        if (resolvedId != null && resolvedId != config.DestinationPlaylistId)
        {
            config.DestinationPlaylistId = resolvedId;
            await SaveConfigurationAsync(config);
        }
        return resolvedId;
    }

    /// <summary>
    /// Lists the current user's followed shows (podcasts), usable as interleave candidates.
    /// </summary>
    public Task<List<FullShow>> GetFollowedShowsAsync() => ListFollowedShowsAsync();

    /// <summary>
    /// Lists the current user's playlists that are valid sources (i.e. not the destination playlist itself).
    /// </summary>
    public async Task<List<FullPlaylist>> GetSourcePlaylistCandidatesAsync(string destinationId)
    {
        var paging = await _spotifyClient.Playlists.CurrentUsers();
        if (paging == null) return new List<FullPlaylist>();

        var allPlaylists = await _spotifyClient.PaginateAll(paging);
        // Exclude by ID, and defensively by name too: if a stale/duplicate "Better Daily Drive"
        // playlist exists with a different ID than the current destination (e.g. left over from
        // before FindOrCreatePlaylistAsync's name match started working reliably), it must never
        // be selectable as a source - that would feed the destination's own curated output back
        // into itself, recreating the exact feedback loop this app exists to avoid.
        return allPlaylists
            .Where(p => p.Id != destinationId)
            .Where(p => !string.Equals(p.Name, TargetPlaylistName, StringComparison.OrdinalIgnoreCase))
            .Cast<FullPlaylist>()
            .ToList();
    }

    /// <summary>
    /// Reads the destination playlist's current contents, for display (e.g. the web UI's home screen).
    /// </summary>
    public async Task<List<TrackSummary>> GetDestinationPlaylistTracksAsync(string playlistId)
    {
        var playlist = await _spotifyClient.Playlists.Get(playlistId);
        if (playlist?.Items?.Items == null) return new List<TrackSummary>();

        var items = await _spotifyClient.PaginateAll(playlist.Items);
        var summaries = new List<TrackSummary>();

        foreach (var item in items)
        {
            switch (item.Track)
            {
                case FullTrack track:
                    summaries.Add(new TrackSummary
                    {
                        Title = track.Name,
                        Subtitle = string.Join(", ", track.Artists.Select(a => a.Name)),
                        ImageUrl = track.Album?.Images?.FirstOrDefault()?.Url,
                        IsEpisode = false
                    });
                    break;
                case FullEpisode episode:
                    summaries.Add(new TrackSummary
                    {
                        Title = episode.Name,
                        Subtitle = "Podcast episode",
                        ImageUrl = episode.Images?.FirstOrDefault()?.Url,
                        IsEpisode = true
                    });
                    break;
            }
        }

        return summaries;
    }

    /// <summary>
    /// Loads the full source playlists/shows for a saved configuration, then curates,
    /// interleaves, and pushes the result to the destination playlist.
    /// </summary>
    public async Task RunPipelineAsync(string destinationPlaylistId, PlaylistConfiguration config)
    {
        // Last-resort guard: never treat the destination playlist as one of its own sources,
        // even if it somehow ended up saved in the configuration (e.g. an older config file, or a
        // stale duplicate playlist that matched by ID before its name was also excluded upstream).
        if (config.SourcePlaylistIds.Remove(destinationPlaylistId))
        {
            Console.WriteLine("Warning: the destination playlist was found in the saved source list and has been excluded.");
        }

        var finalSourcePlaylists = await LoadSourcePlaylistsAsync(config.SourcePlaylistIds);
        var finalSelectedShows = await LoadSelectedShowsAsync(config.ShowIds);

        Console.WriteLine($"\nSelected {finalSourcePlaylists.Count} music source playlist(s) and {finalSelectedShows.Count} show(s).");

        Console.WriteLine($"\n--- Curating and Copying tracks to '{TargetPlaylistName}' ---");
        await ConsolidateAndInterleaveTracksAsync(destinationPlaylistId, finalSourcePlaylists, finalSelectedShows);

        Console.WriteLine("\n--- Operation Complete ---");
        Console.WriteLine($"Successfully consolidated and curated the final playlist '{TargetPlaylistName}'.");
    }
    
    /// <summary>
    /// Waits for a specified duration, checking for user input to abort the auto-run.
    /// FIX: Added check for Console.IsInputRedirected to prevent InvalidOperationException in cron jobs.
    /// </summary>
    /// <returns>True if setup should run, False to use the saved config.</returns>
    private async Task<bool> ShouldRunSetupOrWaitAsync(int timeoutSeconds)
    {
        // CRON FIX: If console input is redirected (typical for cron or automated tasks), 
        // skip the interactive wait entirely and proceed with the saved configuration.
        if (Console.IsInputRedirected)
        {
            Console.WriteLine("Non-interactive environment (cron/redirected input) detected. Proceeding with saved configuration immediately.");
            return false;
        }
        
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
                // Console.KeyAvailable is now safe to call here because we checked for redirection above.
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

    // Config persistence touches only the local JSON file, not the Spotify client, so it's exposed
    // as static and callable without an authenticated PlaylistManager instance (e.g. from a web UI
    // that wants to display the saved setup before the user has logged in).
    public static async Task SaveConfigurationAsync(PlaylistConfiguration config)
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

    public static async Task<PlaylistConfiguration?> LoadConfigurationAsync()
    {
        if (!File.Exists(ConfigFileName))
        {
            return null;
        }
        try
        {
            string jsonString = await File.ReadAllTextAsync(ConfigFileName);
            var config = JsonSerializer.Deserialize<PlaylistConfiguration>(jsonString);
            
            // A config with only a pinned DestinationPlaylistId and no sources yet (written by
            // EnsureDestinationPlaylistAsync before any setup has run) is valid, not corrupt.
            if (config == null || (!config.SourcePlaylistIds.Any() && string.IsNullOrEmpty(config.DestinationPlaylistId)))
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
        var matches = playlists.Where(p => p.Name!.Equals(playlistName, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count > 1)
        {
            // This method only runs when there's no valid pinned ID yet (see EnsureDestinationPlaylistAsync),
            // so picking wrong here only happens once - but pick deterministically anyway and surface the
            // ambiguity loudly, since the real fix is for the user to remove/rename the duplicates.
            Console.WriteLine($"WARNING: Found {matches.Count} playlists named '{playlistName}'. Picking one " +
                "(lowest ID) and pinning it by ID from now on, but you should delete or rename the others in " +
                "the Spotify app to avoid confusion:");
            foreach (var m in matches.OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                Console.WriteLine($"  - https://open.spotify.com/playlist/{m.Id} ({m.Items?.Total ?? 0} tracks)");
            }
        }

        var existingPlaylist = matches.OrderBy(p => p.Id, StringComparer.Ordinal).FirstOrDefault();

        if (existingPlaylist != null)
        {
            Console.WriteLine($"Found existing playlist: {existingPlaylist.Name} ({existingPlaylist.Id})");
            await TrySetCoverImageAsync(existingPlaylist.Id!);
            return existingPlaylist.Id;
        }

        try
        {
            // POST /users/{user_id}/playlists was removed by Spotify; playlists are now always
            // created for the current user via POST /me/playlists (userId is no longer accepted).
            var newPlaylist = await _spotifyClient.Playlists.Create(new PlaylistCreateRequest(playlistName)
            {
                Public = false,
                Description = $"Curated playlist of {MaxTracksToSelect} tracks interleaved with podcasts. Created by Spotify CLI."
            });
            Console.WriteLine($"Created new playlist: {newPlaylist.Name}");
            await TrySetCoverImageAsync(newPlaylist.Id!);
            return newPlaylist.Id;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to create playlist: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Sets the playlist's cover art to the app's bundled image, so it doesn't have to be set manually
    /// in the Spotify app. Runs on every find-or-create, so it will overwrite any cover you set by hand
    /// in the Spotify app; that's a deliberate trade-off since this app already fully owns and rebuilds
    /// this playlist's contents every run - remove this call if you'd rather manage the cover yourself.
    /// </summary>
    private async Task TrySetCoverImageAsync(string playlistId)
    {
        var base64Jpeg = CoverImage.GetJpegBase64();
        if (base64Jpeg == null)
        {
            Console.WriteLine("Skipping playlist cover image (could not prepare it).");
            return;
        }

        try
        {
            await _spotifyClient.Playlists.UploadCover(playlistId, base64Jpeg);
            Console.WriteLine("Playlist cover image set.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not set playlist cover image: {ex.Message}");
        }
    }

    /// <summary>
    /// Prompts the user to select one or more existing playlists to use as track sources.
    /// </summary>
    private async Task<List<FullPlaylist>?> SelectSourcePlaylistsAsync(string destinationId)
    {
        var sourceCandidates = await GetSourcePlaylistCandidatesAsync(destinationId);

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
            Console.WriteLine($"  {i + 1}. {sourcePlaylist.Name} (Tracks: {sourcePlaylist.Items?.Total})");
        }
        
        // CRON SAFETY NOTE: Console.ReadLine() will return an empty string or fail 
        // in a non-interactive environment if no input redirection is set up.
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
            // Spotify removed the 'publisher' field from the Show object; name is all we can show now.
            Console.WriteLine($"  {i + 1}. {shows[i].Name}");
        }
        
        // Read input and parse selection
        // CRON SAFETY NOTE: Console.ReadLine() will return an empty string or fail 
        // in a non-interactive environment if no input redirection is set up.
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
            if (fullPlaylist?.Items?.Items == null) continue;

            // Paginate all tracks from this source playlist
            var allTracks = await _spotifyClient.PaginateAll(fullPlaylist.Items);

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
        
        // 5. REPLACE PLAYLIST CONTENTS ATOMICALLY
        //
        // Previously this cleared the playlist (chunked removes) and then added tracks back
        // (chunked adds), which produces a separate snapshot per chunk on every run. Spotify's
        // own apps reliably push-sync a playlist for only a limited number of snapshots before
        // falling back to a stale cached copy, and the old remove/add pattern burned through
        // that budget every single run. Using ReplacePlaylistItems for the first batch collapses
        // the "clear + first 100" step into one atomic snapshot instead of two-plus.
        Console.WriteLine($"Replacing destination playlist contents with {finalUris.Count} items...");
        var chunks = finalUris.Chunk(100).ToList();
        int addedCount = 0;

        if (chunks.Count == 0)
        {
            var emptyRequest = new PlaylistReplaceItemsRequest(new List<string>());
            await _spotifyClient.Playlists.ReplacePlaylistItems(destinationId, emptyRequest);
        }
        else
        {
            var replaceRequest = new PlaylistReplaceItemsRequest(chunks[0].ToList());
            await _spotifyClient.Playlists.ReplacePlaylistItems(destinationId, replaceRequest);
            addedCount += chunks[0].Length;

            foreach (var chunk in chunks.Skip(1))
            {
                var addRequest = new PlaylistAddItemsRequest(chunk.ToList());
                await _spotifyClient.Playlists.AddPlaylistItems(destinationId, addRequest);
                addedCount += chunk.Length;
            }
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

}

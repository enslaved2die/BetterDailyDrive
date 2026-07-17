// Tracks the web UI's rebuild progress for the /progress endpoint. Percent/status are derived from the
// same Console.WriteLine markers PlaylistManager already prints, so PlaylistManager itself needed no
// changes - this only recognizes known lines as they pass through the console.
using System;

public static class ProgressState
{
    private static readonly object Lock = new();
    private static bool _isRunning;
    private static int _percent;
    private static string _status = "Idle";
    private static int _sourcesSeen;
    private static int _episodesSeen;

    public static void Start()
    {
        lock (Lock)
        {
            _isRunning = true;
            _percent = 0;
            _status = "Starting rebuild...";
            _sourcesSeen = 0;
            _episodesSeen = 0;
        }
    }

    public static void Finish(string status, bool success = true)
    {
        lock (Lock)
        {
            _isRunning = false;
            _status = status;
            _percent = success ? 100 : _percent;
        }
    }

    public static (bool IsRunning, int Percent, string Status) Snapshot()
    {
        lock (Lock)
        {
            return (_isRunning, _percent, _status);
        }
    }

    public static void TryUpdateFromLogLine(string line)
    {
        lock (Lock)
        {
            if (!_isRunning) return;

            if (line.Contains("Selected") && line.Contains("music source playlist"))
            {
                Set(10, "Loading source playlists and podcasts...");
            }
            else if (line.Contains("Fetching all track URIs"))
            {
                Set(15, "Fetching tracks from your source playlists...");
            }
            else if (line.Contains("- Collected") && line.Contains("tracks from"))
            {
                _sourcesSeen++;
                Set(Math.Min(15 + _sourcesSeen * 5, 45), "Fetching tracks from your source playlists...");
            }
            else if (line.Contains("Randomly selected and shuffled"))
            {
                Set(50, "Shuffling and selecting tracks...");
            }
            else if (line.Contains("- Found latest episode from"))
            {
                _episodesSeen++;
                Set(Math.Min(50 + _episodesSeen * 3, 65), "Fetching latest podcast episodes...");
            }
            else if (line.Contains("Interleaved") && line.Contains("podcast episode"))
            {
                Set(70, "Interleaving music and podcasts...");
            }
            else if (line.Contains("Replacing destination playlist contents"))
            {
                Set(80, "Updating your Spotify playlist...");
            }
            else if (line.Contains("Successfully updated playlist"))
            {
                Set(90, "Playlist updated, finishing up...");
            }
            else if (line.Contains("Playlist cover image set") || line.Contains("Could not set playlist cover image")
                     || line.Contains("Skipping playlist cover image"))
            {
                Set(95, "Setting cover art...");
            }
            else if (line.Contains("Operation Complete"))
            {
                Set(100, "Done!");
            }
        }
    }

    private static void Set(int percent, string status)
    {
        _percent = percent;
        _status = status;
    }
}

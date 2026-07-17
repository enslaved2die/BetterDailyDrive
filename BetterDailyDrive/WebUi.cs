// A minimal embedded web UI (launched via `--ui`) that mirrors the console flow in a browser:
// Client ID entry, Spotify login, source playlist / podcast setup, and a rebuild trigger.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpotifyAPI.Web;

public static class WebUi
{
    // Binding to 0.0.0.0 (all interfaces) rather than localhost specifically matters for two reasons:
    // it's what makes this reachable from other devices on the same network at all, and inside Docker
    // it's required for the published port mapping to work in the first place - Docker delivers
    // forwarded traffic to the container's network interface, not its loopback, so a listener bound
    // to localhost inside a container is unreachable even from the host, let alone the LAN.
    private const string ListenUrl = "http://0.0.0.0:5080";

    // Only one login/refresh/rebuild should run at a time - the console flow is inherently
    // single-threaded, but web requests can arrive concurrently.
    private static readonly SemaphoreSlim ActionLock = new(1, 1);

    public static async Task RunAsync(string[] args)
    {
        HookConsoleLog();

        var authManager = new AuthManager();
        string? cachedDestinationId = null;

        // Re-authenticates on every call rather than caching a client indefinitely - the web UI is a
        // long-running process, so an access token obtained at first login will expire (Spotify access
        // tokens last about an hour) long before the process does. GetAuthenticatedClientAsync is cheap
        // when the token is still valid (a local check, no network call) and transparently refreshes it
        // over the network once it's near expiry, so calling it before every action is what makes the
        // token "auto-refresh" in practice instead of only ever working once per process lifetime.
        async Task<PlaylistManager?> EnsureAuthenticatedAsync()
        {
            await ActionLock.WaitAsync();
            try
            {
                var client = await authManager.GetAuthenticatedClientAsync();
                return client != null ? new PlaylistManager(client) : null;
            }
            finally
            {
                ActionLock.Release();
            }
        }

        // Dashboard-only variant: refreshes the token if it's silently refreshable, but never prompts
        // or opens a browser - a page load/refresh should never trigger a surprise interactive login.
        async Task<PlaylistManager?> TryGetAuthenticatedManagerSilentlyAsync()
        {
            await ActionLock.WaitAsync();
            try
            {
                var client = await authManager.TryGetAuthenticatedClientSilentlyAsync();
                return client != null ? new PlaylistManager(client) : null;
            }
            finally
            {
                ActionLock.Release();
            }
        }

        async Task<string?> EnsureDestinationIdAsync(PlaylistManager manager)
        {
            if (cachedDestinationId != null) return cachedDestinationId;
            var userId = await manager.GetCurrentUserIdAsync();
            cachedDestinationId = await manager.EnsureDestinationPlaylistAsync(userId);
            return cachedDestinationId;
        }

        // Shared by the "Rebuild Now" button and the scheduler below, so they can't run concurrently
        // and both drive the same progress bar. Returns false without doing anything if a rebuild is
        // already in flight.
        bool TryStartRebuild(PlaylistManager manager, PlaylistManager.PlaylistConfiguration config)
        {
            if (ProgressState.Snapshot().IsRunning) return false;

            // Set synchronously before the background task starts, so the very next page load (the
            // POST handler's redirect target) already reflects "running" instead of racing with it.
            ProgressState.Start();
            _ = Task.Run(async () =>
            {
                await ActionLock.WaitAsync();
                try
                {
                    var destinationId = await EnsureDestinationIdAsync(manager);
                    if (destinationId != null)
                    {
                        await manager.RunPipelineAsync(destinationId, config);
                    }
                    ProgressState.Finish("Done!");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Rebuild failed: {ex}");
                    ProgressState.Finish($"Failed: {ex.Message}", success: false);
                }
                finally
                {
                    ActionLock.Release();
                }
            });
            return true;
        }

        // Checks once a minute whether the current local time matches one of the configured
        // ScheduledTimes, and if so triggers the same rebuild the "Rebuild Now" button does. Uses the
        // silent (never-interactive) auth path deliberately - an unattended scheduled trigger should
        // never pop a browser out of nowhere; if there's no valid session, it just skips that run and
        // logs why, same as it would for any other silent-auth failure.
        async Task RunSchedulerLoopAsync()
        {
            var lastFiredDate = new Dictionary<string, DateOnly>();
            while (true)
            {
                try
                {
                    var config = await PlaylistManager.LoadConfigurationAsync();
                    if (config != null && config.ScheduledTimes.Count > 0)
                    {
                        var now = DateTime.Now;
                        var nowHm = now.ToString("HH:mm");
                        var today = DateOnly.FromDateTime(now);

                        foreach (var time in config.ScheduledTimes)
                        {
                            if (time != nowHm) continue;
                            if (lastFiredDate.TryGetValue(time, out var last) && last == today) continue;

                            lastFiredDate[time] = today;
                            var manager = await TryGetAuthenticatedManagerSilentlyAsync();
                            if (manager == null)
                            {
                                Console.WriteLine($"Scheduled rebuild for {time} skipped - not logged in.");
                                continue;
                            }

                            Console.WriteLine($"Scheduled rebuild triggered for {time}.");
                            TryStartRebuild(manager, config);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Scheduler check failed: {ex.Message}");
                }

                await Task.Delay(TimeSpan.FromSeconds(30));
            }
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(ListenUrl);
        builder.Logging.ClearProviders(); // keep noise out of the console log panel
        var app = builder.Build();

        // Without this, an unhandled exception in any route (e.g. a transient Spotify API error
        // mid-rebuild) surfaced as a bare, undiagnosable 500 to the browser with nothing printed
        // anywhere - ClearProviders() above also silenced ASP.NET Core's own exception logging.
        // This puts the real exception into the same log the dashboard already shows.
        app.Use(async (ctx, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unhandled error on {ctx.Request.Path}: {ex}");
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = 500;
                    ctx.Response.ContentType = "text/plain";
                    await ctx.Response.WriteAsync(
                        "Something went wrong: " + ex.Message +
                        "\n\nFull details were written to the terminal running this app. Go back to http://localhost:5080/ and try again.");
                }
            }
        });

        app.MapGet("/", async ctx =>
        {
            var snapshot = await authManager.GetAuthSnapshotAsync();
            var config = await PlaylistManager.LoadConfigurationAsync();

            // Silently refresh-if-needed and fetch the current playlist contents, but never pop a
            // browser from a passive page load - if that would be required, just skip the track list.
            // Also never let a passive page load 500 the whole dashboard just because the stored token
            // happened to get rejected by Spotify at this exact moment (e.g. revoked, clock skew) -
            // that's still recoverable via Login, so degrade to "no track list" instead of crashing.
            List<PlaylistManager.TrackSummary>? currentTracks = null;
            try
            {
                var silentManager = await TryGetAuthenticatedManagerSilentlyAsync();
                if (silentManager != null)
                {
                    var destinationId = await EnsureDestinationIdAsync(silentManager);
                    if (destinationId != null)
                    {
                        currentTracks = await silentManager.GetDestinationPlaylistTracksAsync(destinationId);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not load current playlist for dashboard: {ex.Message}");
            }

            await ctx.Response.WriteAsync(Html("Better Daily Drive", RenderDashboard(snapshot, config, currentTracks)));
        });

        app.MapPost("/client-id", async ctx =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var clientId = form["clientId"].ToString().Trim();
            if (!string.IsNullOrEmpty(clientId))
            {
                await authManager.SetClientIdAsync(clientId);
            }
            ctx.Response.Redirect("/");
        });

        app.MapGet("/login", async ctx =>
        {
            await EnsureAuthenticatedAsync();
            ctx.Response.Redirect("/");
        });

        app.MapGet("/setup", async ctx =>
        {
            var manager = await EnsureAuthenticatedAsync();
            if (manager == null) { ctx.Response.Redirect("/"); return; }

            var destinationId = await EnsureDestinationIdAsync(manager);
            var candidates = destinationId != null
                ? await manager.GetSourcePlaylistCandidatesAsync(destinationId)
                : new List<FullPlaylist>();
            var shows = await manager.GetFollowedShowsAsync();
            var existingConfig = await PlaylistManager.LoadConfigurationAsync();

            await ctx.Response.WriteAsync(Html("Setup", RenderSetupForm(candidates, shows, existingConfig)));
        });

        app.MapPost("/setup", async ctx =>
        {
            var manager = await EnsureAuthenticatedAsync();
            if (manager == null) { ctx.Response.Redirect("/"); return; }

            var form = await ctx.Request.ReadFormAsync();
            var config = new PlaylistManager.PlaylistConfiguration
            {
                SourcePlaylistIds = SplitCsv(form["playlistIds"].ToString()),
                // ShowIds is order-significant (interleave sequence) - comes from a hidden input
                // built by click order in the browser, not checkbox DOM order.
                ShowIds = SplitCsv(form["showOrder"].ToString())
            };
            await PlaylistManager.SaveConfigurationAsync(config);
            ctx.Response.Redirect("/");
        });

        app.MapPost("/run", async ctx =>
        {
            var manager = await EnsureAuthenticatedAsync();
            if (manager == null) { ctx.Response.Redirect("/"); return; }

            var config = await PlaylistManager.LoadConfigurationAsync();
            if (config == null) { ctx.Response.Redirect("/setup"); return; }

            // Runs in the background rather than being awaited here: the whole point of the progress
            // bar is to show live status while the rebuild is in flight, which requires this request
            // to return immediately instead of blocking until the pipeline finishes.
            TryStartRebuild(manager, config);
            ctx.Response.Redirect("/");
        });

        app.MapGet("/progress", async ctx =>
        {
            var (isRunning, percent, status) = ProgressState.Snapshot();
            await ctx.Response.WriteAsJsonAsync(new { isRunning, percent, status });
        });

        app.MapGet("/settings", async ctx =>
        {
            var manager = await EnsureAuthenticatedAsync();
            if (manager == null) { ctx.Response.Redirect("/"); return; }

            var config = await PlaylistManager.LoadConfigurationAsync() ?? new PlaylistManager.PlaylistConfiguration();
            await ctx.Response.WriteAsync(Html("Settings", RenderSettingsForm(config)));
        });

        app.MapPost("/settings/add", async ctx =>
        {
            var manager = await EnsureAuthenticatedAsync();
            if (manager == null) { ctx.Response.Redirect("/"); return; }

            var form = await ctx.Request.ReadFormAsync();
            var time = form["time"].ToString().Trim();
            var config = await PlaylistManager.LoadConfigurationAsync() ?? new PlaylistManager.PlaylistConfiguration();

            if (System.Text.RegularExpressions.Regex.IsMatch(time, @"^([01]\d|2[0-3]):[0-5]\d$")
                && !config.ScheduledTimes.Contains(time))
            {
                config.ScheduledTimes.Add(time);
                config.ScheduledTimes.Sort(StringComparer.Ordinal); // "HH:mm" sorts correctly as plain text
                await PlaylistManager.SaveConfigurationAsync(config);
            }

            ctx.Response.Redirect("/settings");
        });

        app.MapPost("/settings/remove", async ctx =>
        {
            var manager = await EnsureAuthenticatedAsync();
            if (manager == null) { ctx.Response.Redirect("/"); return; }

            var form = await ctx.Request.ReadFormAsync();
            var time = form["time"].ToString().Trim();
            var config = await PlaylistManager.LoadConfigurationAsync() ?? new PlaylistManager.PlaylistConfiguration();
            config.ScheduledTimes.Remove(time);
            await PlaylistManager.SaveConfigurationAsync(config);

            ctx.Response.Redirect("/settings");
        });

        _ = Task.Run(RunSchedulerLoopAsync);

        Console.WriteLine("Web UI running on port 5080 - open http://localhost:5080 on this machine, " +
            "or http://<this machine's IP>:5080 from another device on the same network. Press Ctrl+C to stop.");
        await app.RunAsync();
    }

    private static List<string> SplitCsv(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    // Recognizes the existing Console.WriteLine markers PlaylistManager already emits during a rebuild
    // and maps them to a percent/status pair for the progress bar - no changes needed to PlaylistManager
    // itself, and the console still gets every line as before (this only taps the stream, doesn't replace it).
    private static void HookConsoleLog()
    {
        var original = Console.Out;
        Console.SetOut(new TeeTextWriter(original, line => ProgressState.TryUpdateFromLogLine(line)));
    }

    // A small inline SVG used whenever a playlist/show/track has no cover art of its own.
    private const string PlaceholderCover =
        "data:image/svg+xml;utf8," +
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'>" +
        "<rect width='100' height='100' fill='%23282828'/>" +
        "<path d='M35 70V32l30-6v34' stroke='%23727272' stroke-width='4' fill='none'/>" +
        "<circle cx='31' cy='72' r='7' fill='%23727272'/><circle cx='61' cy='64' r='7' fill='%23727272'/>" +
        "</svg>";

    private static string RenderDashboard(
        (bool HasClientId, bool HasToken, DateTime? ExpiresAt) snapshot,
        PlaylistManager.PlaylistConfiguration? config,
        List<PlaylistManager.TrackSummary>? currentTracks)
    {
        var sb = new StringBuilder();
        sb.Append("<div class='content'>");

        if (!snapshot.HasClientId)
        {
            sb.Append("<h1>Connect your Spotify app</h1>");
            sb.Append("<form method='post' action='/client-id' class='inline-form'>");
            sb.Append("<input name='clientId' placeholder='Spotify Client ID' required>");
            sb.Append("<button class='btn-primary' type='submit'>Save</button></form>");
            sb.Append("<p class='muted'>Get one from the <a href='https://developer.spotify.com/dashboard' target='_blank'>Spotify Developer Dashboard</a>.</p>");
        }
        else if (!snapshot.HasToken)
        {
            sb.Append("<h1>Log in to Spotify</h1>");
            sb.Append("<p><a href='/login'><button class='btn-primary'>Login with Spotify</button></a></p>");
            sb.Append("<p class='muted'>This opens your browser to Spotify's login page.</p>");
        }
        else
        {
            sb.Append("<div class='row-between'>");
            sb.Append("<div><h1>Better Daily Drive</h1>");
            if (config == null)
            {
                sb.Append("<p class='muted'>No setup saved yet.</p>");
            }
            else
            {
                sb.Append("<p class='muted'>")
                  .Append(config.SourcePlaylistIds.Count).Append(" source playlist(s) &middot; ")
                  .Append(config.ShowIds.Count).Append(" podcast(s)</p>");
            }
            sb.Append("</div>");
            sb.Append("<div class='actions'>");
            sb.Append("<a href='/setup'><button class='btn-outline'>Edit Setup</button></a>");
            sb.Append("<a href='/settings'><button class='btn-outline'>Settings</button></a>");
            sb.Append("<form method='post' action='/run' id='runForm'><button class='btn-primary' id='runButton'>Rebuild Now</button></form>");
            sb.Append("</div></div>");

            var progress = ProgressState.Snapshot();
            sb.Append("<div id='progressSection' style='display:").Append(progress.IsRunning ? "block" : "none").Append("'>");
            sb.Append("<div class='progress-track'><div class='progress-fill' id='progressFill' style='width:")
              .Append(progress.Percent).Append("%'></div></div>");
            sb.Append("<p class='muted' id='progressStatus'>").Append(Encode(progress.Status)).Append("</p>");
            sb.Append("</div>");
            sb.Append("<script>").Append(ProgressScript(progress.IsRunning)).Append("</script>");

            sb.Append("<h2>Current playlist</h2>");
            if (currentTracks == null || currentTracks.Count == 0)
            {
                sb.Append("<p class='muted'>Empty, or not built yet - hit Rebuild Now.</p>");
            }
            else
            {
                sb.Append("<div class='tracklist'>");
                for (int i = 0; i < currentTracks.Count; i++)
                {
                    var t = currentTracks[i];
                    sb.Append("<div class='track-row").Append(t.IsEpisode ? " episode" : "").Append("'>");
                    sb.Append("<span class='track-index'>").Append(i + 1).Append("</span>");
                    sb.Append("<img class='track-thumb' src='").Append(Encode(t.ImageUrl ?? PlaceholderCover)).Append("'>");
                    sb.Append("<span class='track-meta'>");
                    sb.Append("<span class='track-title'>").Append(Encode(t.Title)).Append("</span>");
                    sb.Append("<span class='track-subtitle'>").Append(Encode(t.Subtitle)).Append("</span>");
                    sb.Append("</span>");
                    if (t.IsEpisode) sb.Append("<span class='pill'>Podcast</span>");
                    sb.Append("</div>");
                }
                sb.Append("</div>");
            }
        }

        sb.Append("</div>");
        return sb.ToString();
    }

    private static string ProgressScript(bool startRunning) => $$"""
        (function() {
            const section = document.getElementById('progressSection');
            const fill = document.getElementById('progressFill');
            const status = document.getElementById('progressStatus');
            const runForm = document.getElementById('runForm');
            const runButton = document.getElementById('runButton');
            let polling = {{(startRunning ? "true" : "false")}};

            function setButtonDisabled(disabled) {
                if (runButton) runButton.disabled = disabled;
            }

            function poll() {
                fetch('/progress').then(r => r.json()).then(data => {
                    if (!section || !fill || !status) return;
                    section.style.display = data.isRunning || data.percent > 0 ? 'block' : 'none';
                    fill.style.width = data.percent + '%';
                    status.textContent = data.status;
                    if (data.isRunning) {
                        setButtonDisabled(true);
                        setTimeout(poll, 700);
                    } else {
                        setButtonDisabled(false);
                        if (polling) { polling = false; location.reload(); }
                    }
                }).catch(() => setTimeout(poll, 1500));
            }

            if (runForm) {
                runForm.addEventListener('submit', function(e) {
                    e.preventDefault();
                    setButtonDisabled(true);
                    fetch('/run', { method: 'POST' }).then(() => { polling = true; poll(); });
                });
            }

            if (polling) poll();
        })();
        """;

    private static string RenderSettingsForm(PlaylistManager.PlaylistConfiguration config)
    {
        var sb = new StringBuilder();
        sb.Append("<div class='content'>");
        sb.Append("<h1>Settings</h1>");

        sb.Append("<h2>Automatic rebuild times</h2>");
        sb.Append("<p class='muted'>The playlist rebuilds automatically at these times every day, in this server's local time zone. ")
          .Append("Only happens while this app is actually running - if it's off at the scheduled time, that run is simply skipped.</p>");

        var times = config.ScheduledTimes.OrderBy(t => t, StringComparer.Ordinal).ToList();
        if (!times.Any())
        {
            sb.Append("<p class='muted'>No scheduled times yet - the playlist only rebuilds when you press Rebuild Now.</p>");
        }
        else
        {
            sb.Append("<div class='schedule-list'>");
            foreach (var time in times)
            {
                sb.Append("<div class='schedule-row'>");
                sb.Append("<span class='schedule-time'>").Append(Encode(time)).Append("</span>");
                sb.Append("<form method='post' action='/settings/remove'>");
                sb.Append("<input type='hidden' name='time' value='").Append(Encode(time)).Append("'>");
                sb.Append("<button type='submit' class='btn-outline'>Remove</button>");
                sb.Append("</form>");
                sb.Append("</div>");
            }
            sb.Append("</div>");
        }

        sb.Append("<form method='post' action='/settings/add' class='inline-form'>");
        sb.Append("<input type='time' name='time' required>");
        sb.Append("<button class='btn-primary' type='submit'>Add time</button>");
        sb.Append("</form>");

        sb.Append("<p style='margin-top:24px'><a href='/'><button type='button' class='btn-outline'>Back</button></a></p>");
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string RenderSetupForm(List<FullPlaylist> candidates, List<FullShow> shows, PlaylistManager.PlaylistConfiguration? existingConfig)
    {
        var selectedPlaylistIds = existingConfig?.SourcePlaylistIds ?? new List<string>();
        // Order matters for shows: preserve the saved interleave sequence as the initial click order.
        var selectedShowOrder = existingConfig?.ShowIds ?? new List<string>();

        var sb = new StringBuilder();
        sb.Append("<div class='content'>");
        sb.Append("<h1>Setup</h1>");
        sb.Append("<form method='post' action='/setup' id='setupForm'>");

        sb.Append("<h2>Source playlists (music)</h2>");
        sb.Append("<p class='muted'>Click a cover to toggle it as a source.</p>");
        if (!candidates.Any())
        {
            sb.Append("<p class='muted'>No other playlists found.</p>");
        }
        sb.Append("<div class='grid'>");
        foreach (var playlist in candidates)
        {
            var id = playlist.Id ?? string.Empty;
            var isSelected = selectedPlaylistIds.Contains(id);
            var cover = playlist.Images?.FirstOrDefault()?.Url ?? PlaceholderCover;
            sb.Append("<div class='cover-toggle").Append(isSelected ? " selected" : "").Append("' data-id='")
              .Append(Encode(id)).Append("' onclick='togglePlaylist(this)'>");
            sb.Append("<div class='cover-wrap'><img src='").Append(Encode(cover)).Append("'>");
            sb.Append("<div class='check'>&#10003;</div></div>");
            sb.Append("<div class='cover-title'>").Append(Encode(playlist.Name ?? "(unnamed)")).Append("</div>");
            sb.Append("<div class='cover-subtitle'>").Append(playlist.Items?.Total).Append(" tracks</div>");
            sb.Append("</div>");
        }
        sb.Append("</div>");

        sb.Append("<h2>Podcasts to interleave</h2>");
        sb.Append("<p class='muted'>Click covers in the order you want episodes inserted - the number shows the sequence.</p>");
        if (!shows.Any())
        {
            sb.Append("<p class='muted'>You're not following any shows.</p>");
        }
        sb.Append("<div class='grid'>");
        foreach (var show in shows)
        {
            var cover = show.Images?.FirstOrDefault()?.Url ?? PlaceholderCover;
            sb.Append("<div class='cover-toggle' data-id='").Append(Encode(show.Id)).Append("' data-type='show' onclick='toggleShow(this)'>");
            sb.Append("<div class='cover-wrap'><img src='").Append(Encode(cover)).Append("'>");
            sb.Append("<div class='badge'></div></div>");
            sb.Append("<div class='cover-title'>").Append(Encode(show.Name)).Append("</div>");
            sb.Append("</div>");
        }
        sb.Append("</div>");

        sb.Append("<input type='hidden' name='playlistIds' id='playlistIdsInput' value='")
          .Append(Encode(string.Join(',', selectedPlaylistIds))).Append("'>");
        sb.Append("<input type='hidden' name='showOrder' id='showOrderInput' value='")
          .Append(Encode(string.Join(',', selectedShowOrder))).Append("'>");

        sb.Append("<div class='actions' style='margin-top:24px'>");
        sb.Append("<button class='btn-primary' type='submit'>Save Setup</button> ");
        sb.Append("<a href='/'><button type='button' class='btn-outline'>Cancel</button></a>");
        sb.Append("</div></form></div>");

        sb.Append("<script>").Append(SetupScript(selectedPlaylistIds, selectedShowOrder)).Append("</script>");
        return sb.ToString();
    }

    private static string SetupScript(List<string> selectedPlaylistIds, List<string> selectedShowOrder) => $$"""
        let playlistSelection = {{ToJsStringArray(selectedPlaylistIds)}};
        let showOrder = {{ToJsStringArray(selectedShowOrder)}};

        function togglePlaylist(el) {
            const id = el.dataset.id;
            const idx = playlistSelection.indexOf(id);
            if (idx >= 0) { playlistSelection.splice(idx, 1); el.classList.remove('selected'); }
            else { playlistSelection.push(id); el.classList.add('selected'); }
            document.getElementById('playlistIdsInput').value = playlistSelection.join(',');
        }

        function renderShowBadges() {
            document.querySelectorAll(".cover-toggle[data-type='show']").forEach(el => {
                const id = el.dataset.id;
                const idx = showOrder.indexOf(id);
                const badge = el.querySelector('.badge');
                if (idx >= 0) {
                    el.classList.add('selected');
                    badge.style.display = 'flex';
                    badge.textContent = idx + 1;
                } else {
                    el.classList.remove('selected');
                    badge.style.display = 'none';
                }
            });
            document.getElementById('showOrderInput').value = showOrder.join(',');
        }

        function toggleShow(el) {
            const id = el.dataset.id;
            const idx = showOrder.indexOf(id);
            if (idx >= 0) showOrder.splice(idx, 1);
            else showOrder.push(id);
            renderShowBadges();
        }

        renderShowBadges();
        """;

    private static string ToJsStringArray(List<string> values) =>
        "[" + string.Join(",", values.Select(v => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "]";

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    // Spotify-inspired dark theme: near-black background, Spotify green accents, pill buttons,
    // and cover-art grids that double as the selection controls.
    private const string Style = """
        :root { color-scheme: dark; }
        * { box-sizing: border-box; }
        body {
            font-family: "Helvetica Neue", Helvetica, Arial, system-ui, sans-serif;
            background: #121212; color: #fff; margin: 0; padding: 0;
        }
        .content { max-width: 900px; margin: 0 auto; padding: 24px 32px 64px; }
        h1 { font-size: 2em; margin: 0 0 4px; }
        h2 { font-size: 1.3em; margin: 32px 0 4px; }
        .muted { color: #a7a7a7; font-size: 0.9em; }
        a { color: inherit; text-decoration: none; }
        .row-between { display: flex; justify-content: space-between; align-items: flex-end; flex-wrap: wrap; gap: 16px; }
        .actions { display: flex; gap: 12px; align-items: center; }
        .actions form { margin: 0; }
        button {
            border: none; border-radius: 500px; padding: 10px 24px; font-size: 0.95em;
            font-weight: 700; cursor: pointer; font-family: inherit;
        }
        button:disabled { opacity: 0.5; cursor: not-allowed; }
        .btn-primary { background: #1DB954; color: #000; }
        .btn-primary:hover { background: #1ed760; }
        .btn-outline { background: transparent; color: #fff; border: 1px solid #727272; }
        .btn-outline:hover { border-color: #fff; }
        .inline-form { display: flex; gap: 8px; margin: 16px 0; }
        .inline-form input {
            background: #2a2a2a; border: 1px solid #3e3e3e; color: #fff; border-radius: 4px;
            padding: 10px 12px; font-size: 0.95em; min-width: 320px;
        }

        .grid {
            display: grid; grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
            gap: 20px; margin: 16px 0;
        }
        .cover-toggle { cursor: pointer; user-select: none; }
        .cover-wrap { position: relative; border-radius: 6px; overflow: hidden; box-shadow: 0 4px 12px rgba(0,0,0,0.4); }
        .cover-wrap img { width: 100%; aspect-ratio: 1; object-fit: cover; display: block; background: #282828; }
        .cover-toggle .cover-title {
            margin-top: 8px; font-size: 0.9em; font-weight: 600; color: #fff;
            white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
        }
        .cover-toggle .cover-subtitle { font-size: 0.78em; color: #a7a7a7; }
        .cover-toggle .check {
            position: absolute; inset: 0; display: flex; align-items: center; justify-content: center;
            background: rgba(29,185,84,0.55); color: #fff; font-size: 2em; opacity: 0; transition: opacity 0.1s;
        }
        .cover-toggle.selected .check { opacity: 1; }
        .cover-toggle.selected .cover-wrap { outline: 3px solid #1DB954; }
        .cover-toggle .badge {
            position: absolute; top: 8px; left: 8px; width: 26px; height: 26px; border-radius: 50%;
            background: #1DB954; color: #000; font-weight: 700; font-size: 0.85em;
            display: none; align-items: center; justify-content: center;
        }

        .tracklist { margin-top: 8px; }
        .track-row {
            display: flex; align-items: center; gap: 12px; padding: 6px 8px; border-radius: 4px;
        }
        .track-row:hover { background: #262626; }
        .track-index { width: 20px; text-align: right; color: #a7a7a7; font-size: 0.85em; }
        .track-thumb { width: 40px; height: 40px; object-fit: cover; border-radius: 3px; background: #282828; }
        .track-meta { display: flex; flex-direction: column; overflow: hidden; flex: 1; }
        .track-title { font-size: 0.95em; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .track-subtitle { font-size: 0.8em; color: #a7a7a7; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .track-row.episode .track-title { color: #1ed760; }
        .pill { background: #2a2a2a; color: #a7a7a7; font-size: 0.7em; padding: 3px 10px; border-radius: 500px; }

        .progress-track {
            background: #2a2a2a; border-radius: 500px; height: 8px; overflow: hidden; margin-top: 16px;
        }
        .progress-fill {
            background: #1DB954; height: 100%; border-radius: 500px; transition: width 0.4s ease;
        }
        #progressStatus { margin-top: 8px; }

        .schedule-list { margin: 16px 0; display: flex; flex-direction: column; gap: 8px; max-width: 320px; }
        .schedule-row {
            display: flex; align-items: center; justify-content: space-between; gap: 12px;
            background: #181818; border-radius: 6px; padding: 10px 16px;
        }
        .schedule-row form { margin: 0; }
        .schedule-time { font-size: 1.1em; font-weight: 600; font-variant-numeric: tabular-nums; }
        """;

    private static string Html(string title, string body) =>
        "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
        + "<title>" + Encode(title) + "</title><style>" + Style + "</style></head><body>" + body + "</body></html>";

    // Adapts Console.WriteLine so every line also reaches the in-memory log the dashboard renders.
    private sealed class TeeTextWriter : System.IO.TextWriter
    {
        private readonly System.IO.TextWriter _inner;
        private readonly Action<string> _onLine;
        private readonly StringBuilder _lineBuffer = new();

        public TeeTextWriter(System.IO.TextWriter inner, Action<string> onLine)
        {
            _inner = inner;
            _onLine = onLine;
        }

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            _inner.Write(value);
            _lineBuffer.Append(value);
            if (value == '\n')
            {
                _onLine(_lineBuffer.ToString());
                _lineBuffer.Clear();
            }
        }

        public override void Write(string? value)
        {
            if (value == null) return;
            foreach (var c in value) Write(c);
        }
    }
}

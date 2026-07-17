// Manages the Spotify OAuth 2.0 flow (PKCE), token persistence, and refreshing.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;
using System.Net.Http;
using System.Threading;

public class AuthManager
{
    // --- CONFIGURATION (Auth related) ---
    private const int CallbackPort = 58739;
    private static readonly Uri CallbackUri = new Uri($"http://127.0.0.1:{CallbackPort}/callback");
    private const string TokenFilePath = "spotify_auth_data.json";
    
    // Scopes needed for the application
    public static readonly List<string> Scopes = new List<string>
    {
        SpotifyAPI.Web.Scopes.UserReadEmail,
        SpotifyAPI.Web.Scopes.UserLibraryRead,
        SpotifyAPI.Web.Scopes.UserReadPrivate,
        SpotifyAPI.Web.Scopes.PlaylistReadPrivate,
        SpotifyAPI.Web.Scopes.UserFollowRead,
        SpotifyAPI.Web.Scopes.PlaylistModifyPublic,
        SpotifyAPI.Web.Scopes.PlaylistModifyPrivate,
        SpotifyAPI.Web.Scopes.UgcImageUpload
    };

    // This object holds all persistent data (Client ID, Access Token, Refresh Token)
    private AuthData _authData = new AuthData();

    /// <summary>
    /// Loads stored auth data, ensures Client ID is present, refreshes token if necessary, or starts a new
    /// authentication flow (opening a browser) if needed. Returns a ready-to-use ISpotifyClient or null.
    /// Safe to call repeatedly/often - if the token is still valid it's a local check with no network call;
    /// if it's within 5 minutes of expiring, it transparently refreshes it over the network first.
    /// </summary>
    public Task<ISpotifyClient?> GetAuthenticatedClientAsync() => GetClientCoreAsync(allowInteractiveLogin: true);

    /// <summary>
    /// Same as <see cref="GetAuthenticatedClientAsync"/>, but never prompts on the console or opens a
    /// browser - it returns null instead whenever a full (re-)login would otherwise be required. Use this
    /// from passive/read-only contexts (e.g. a dashboard page load) where popping a browser unprompted
    /// would be surprising; it still performs a silent token refresh when the token is merely near expiry.
    /// </summary>
    public Task<ISpotifyClient?> TryGetAuthenticatedClientSilentlyAsync() => GetClientCoreAsync(allowInteractiveLogin: false);

    private async Task<ISpotifyClient?> GetClientCoreAsync(bool allowInteractiveLogin)
    {
        // 1. Load data
        _authData = await LoadAuthDataAsync();

        // 2. Ensure Client ID is set (will prompt user and save if needed, when allowed)
        if (string.IsNullOrEmpty(_authData.ClientId))
        {
            if (!allowInteractiveLogin) return null;
            await EnsureClientIdAsync();
        }

        if (string.IsNullOrEmpty(_authData.ClientId))
        {
            Console.WriteLine("Client ID is missing. Cannot proceed with authentication.");
            return null;
        }

        // 3. Check token state and attempt refresh/re-auth
        // Check if token needs refresh/re-auth (within 5 minutes of expiration)
        if (!_authData.HasToken || _authData.ExpiresAt < DateTime.Now.AddMinutes(5))
        {
            // Token is null or near expiration, attempt to refresh or start new flow
            if (!string.IsNullOrEmpty(_authData.RefreshToken))
            {
                Console.WriteLine("Token expired or close to expiry. Attempting to refresh...");
                if (!await RefreshTokenAsync(_authData.RefreshToken))
                {
                    // Spotify now expires refresh tokens ~6 months after the original authorization
                    // (previously they were effectively permanent), so this now happens periodically
                    // in normal operation, not just on first-time corruption.
                    Console.WriteLine("Refresh failed.");
                    if (!allowInteractiveLogin) return null;
                    if (Console.IsInputRedirected)
                    {
                        Console.WriteLine("Non-interactive environment (cron/redirected input) detected. " +
                            "Cannot start an interactive browser login here. Run this app manually once " +
                            "to re-authenticate, then cron runs will work again until the refresh token expires.");
                        return null;
                    }
                    Console.WriteLine("Starting new Authorization Flow...");
                    return await StartAuthenticationFlowAsync();
                }
            }
            else
            {
                if (!allowInteractiveLogin) return null;
                Console.WriteLine("No valid token found. Starting new Authorization Flow...");
                if (Console.IsInputRedirected)
                {
                    Console.WriteLine("Non-interactive environment (cron/redirected input) detected. " +
                        "Cannot start an interactive browser login here. Run this app manually once to authenticate.");
                    return null;
                }
                return await StartAuthenticationFlowAsync();
            }
        }
        else
        {
            Console.WriteLine("Valid token loaded from storage.");
        }

        // 4. If a valid token (loaded or refreshed) exists, return the client
        if (_authData.HasToken)
        {
            var config = SpotifyClientConfig.CreateDefault().WithToken(_authData.AccessToken);
            return new SpotifyClient(config);
        }

        return null;
    }

    /// <summary>
    /// Reads persisted auth state without prompting for anything or touching the console -
    /// safe for a UI to call just to decide what to show (e.g. before starting a login).
    /// </summary>
    public async Task<(bool HasClientId, bool HasToken, DateTime? ExpiresAt)> GetAuthSnapshotAsync()
    {
        var data = await LoadAuthDataAsync();
        return (!string.IsNullOrEmpty(data.ClientId), data.HasToken, data.HasToken ? data.ExpiresAt : (DateTime?)null);
    }

    /// <summary>
    /// Sets and persists the Client ID without touching the console (used by the web UI's setup form,
    /// where there is no console to prompt via <see cref="EnsureClientIdAsync"/>).
    /// </summary>
    public async Task SetClientIdAsync(string clientId)
    {
        _authData = await LoadAuthDataAsync();
        _authData.ClientId = clientId;
        await SaveAuthDataAsync(_authData);
    }

    /// <summary>
    /// Prompts the user for the Spotify Client ID if it's missing from the configuration.
    /// </summary>
    public async Task EnsureClientIdAsync()
    {
        if (!string.IsNullOrEmpty(_authData.ClientId))
        {
            Console.WriteLine($"Client ID loaded: {_authData.ClientId}");
            return;
        }

        Console.WriteLine("\n--- Initial Setup: Spotify Application Credentials ---");
        Console.WriteLine("The **Client ID** is required to connect to Spotify.");
        Console.WriteLine("You can get this from your Spotify Developer Dashboard: https://developer.spotify.com/dashboard");
        
        string? input = null;
        while (string.IsNullOrEmpty(input))
        {
            Console.Write("Please enter your Client ID: ");
            input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input))
            {
                Console.WriteLine("Client ID cannot be empty. Please try again.");
            }
        }
        _authData.ClientId = input;
        Console.WriteLine("Client ID captured.");
        
        // Save the config immediately so the Client ID is persisted even if the auth flow fails later.
        await SaveAuthDataAsync(_authData);
    }

    /// <summary>
    /// Starts the Authorization Code Flow with PKCE using a local HTTP server.
    /// </summary>
    private async Task<ISpotifyClient?> StartAuthenticationFlowAsync()
    {
        EmbedIOAuthServer? server = null;
        
        try
        {
            Console.WriteLine($"Attempting to start local server on port {CallbackPort}...");
            server = new EmbedIOAuthServer(CallbackUri, CallbackPort);
            await server.Start();
            Console.WriteLine($"Server successfully started on {CallbackUri}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FATAL: Failed to start local server on port {CallbackPort}. Error: {ex.Message}");
            return null;
        }

        (string codeVerifier, string codeChallenge) = PKCEUtil.GenerateCodes();

        var loginRequest = new LoginRequest(
            CallbackUri,
            _authData.ClientId, // Use stored Client ID
            LoginRequest.ResponseType.Code
        )
        {
            Scope = Scopes, 
            CodeChallenge = codeChallenge,
            CodeChallengeMethod = "S256"
        };

        var uri = loginRequest.ToUri();
        Console.WriteLine($"\nOpening browser for authorization. Please login:\n{uri}");

        try
        {
            BrowserUtil.Open(uri);
        }
        catch (Exception ex)
        {
            // No browser to open in a headless/containerized environment (e.g. inside Docker, where
            // xdg-open doesn't exist) - the URL above was already printed, so open it manually instead
            // of letting this crash the whole login attempt.
            Console.WriteLine($"Could not automatically open a browser ({ex.Message}). Open the URL above manually.");
        }

        var codeTcs = new TaskCompletionSource<string>();
        
        server.AuthorizationCodeReceived += (s, response) =>
        {
            codeTcs.SetResult(response.Code);
            return Task.CompletedTask;
        };

        server.ErrorReceived += (s, error, state) =>
        {
            codeTcs.SetException(new Exception($"Authorization failed: {error} (State: {state})"));
            return Task.CompletedTask;
        };
        
        string authCode;
        try
        {
            // Use a cancellation token to prevent hanging indefinitely
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2)); 
            authCode = await codeTcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
             Console.WriteLine("\nAuthentication flow timed out. Please try again.");
             return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Authorization flow interrupted: {ex.Message}");
            return null;
        }
        finally
        {
            if (server != null)
            {
                await server.Stop();
            }
        }

        var initialResponse = await new OAuthClient().RequestToken(
            new PKCETokenRequest(_authData.ClientId, authCode, CallbackUri, codeVerifier) // Use stored Client ID
        );

        if (!string.IsNullOrEmpty(initialResponse.AccessToken))
        {
            Console.WriteLine("Successfully received access and refresh tokens.");
            
            // Update AuthData state
            _authData.AccessToken = initialResponse.AccessToken;
            _authData.RefreshToken = initialResponse.RefreshToken;
            _authData.TokenType = initialResponse.TokenType;
            _authData.Scope = initialResponse.Scope.Split(' ');
            _authData.ExpiresAt = DateTime.Now.AddSeconds(initialResponse.ExpiresIn);
            
            await SaveAuthDataAsync(_authData);

            var config = SpotifyClientConfig.CreateDefault().WithToken(_authData.AccessToken);
            return new SpotifyClient(config);
        }
        else
        {
            Console.WriteLine("Token exchange failed: The Spotify API did not return an access token.");
            return null;
        }
    }

    /// <summary>
    /// Uses the refresh token to get a new access token.
    /// </summary>
    private async Task<bool> RefreshTokenAsync(string refreshToken)
    {
        try
        {
            var newResponse = await new OAuthClient().RequestToken(
                new PKCETokenRefreshRequest(_authData.ClientId, refreshToken) // Use stored Client ID
            );

            if (!string.IsNullOrEmpty(newResponse.AccessToken))
            {
                Console.WriteLine("Token refreshed successfully.");

                // Update AuthData state
                _authData.AccessToken = newResponse.AccessToken;
                // Use new refresh token if provided, otherwise keep the old one
                _authData.RefreshToken = newResponse.RefreshToken ?? refreshToken;
                _authData.TokenType = newResponse.TokenType;
                _authData.Scope = newResponse.Scope.Split(' ');
                _authData.ExpiresAt = DateTime.Now.AddSeconds(newResponse.ExpiresIn);

                await SaveAuthDataAsync(_authData);
                return true;
            }
            return false;
        }
        catch (APIException ex) when (ex.Message?.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Spotify's token endpoint returns invalid_grant once the refresh token is no longer
            // usable (expired, revoked, or rotated out). It will keep failing on retry, so the
            // stored token must be discarded and the user must re-authenticate from scratch.
            Console.WriteLine("Refresh token is no longer valid (invalid_grant) - it has expired or been revoked.");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during token refresh: {ex.Message}");
            return false;
        }
    }

    // =========================================================================
    // --- PERSISTENCE LAYER (Local File Simulation) ---
    // =========================================================================

    private async Task<AuthData> LoadAuthDataAsync()
    {
        if (!File.Exists(TokenFilePath))
        {
            return new AuthData();
        }

        try
        {
            var json = await File.ReadAllTextAsync(TokenFilePath);
            // Use Newtonsoft.Json
            var data = JsonConvert.DeserializeObject<AuthData>(json);
            
            if (data != null)
            {
                Console.WriteLine($"Auth data loaded from {TokenFilePath}.");
                return data;
            }
            throw new Exception("Deserialization resulted in null data.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not load auth data file: {ex.Message}");
            File.Delete(TokenFilePath); 
            return new AuthData();
        }
    }

    private static async Task SaveAuthDataAsync(AuthData data)
    {
        try
        {
            // Use Newtonsoft.Json
            var json = JsonConvert.SerializeObject(data, Formatting.Indented);
            await File.WriteAllTextAsync(TokenFilePath, json);
            Console.WriteLine($"Auth data successfully saved/updated to {TokenFilePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not save auth data file: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Helper class to hold and serialize all authentication data (Client ID and tokens).
    /// </summary>
    public class AuthData
    {
        public string ClientId { get; set; } = string.Empty; // Now persisted
        public string AccessToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public string TokenType { get; set; } = string.Empty;
        public string[] Scope { get; set; } = Array.Empty<string>();
        public DateTime ExpiresAt { get; set; }
        
        public bool HasToken => !string.IsNullOrEmpty(AccessToken);
    }
}

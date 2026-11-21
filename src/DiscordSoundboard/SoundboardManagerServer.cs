using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Serilog;

namespace DiscordSoundboard;

public sealed class SoundboardManagerServer : IAsyncDisposable
{
	private readonly DiscordIpcClient _discordClient;
	private readonly ILogger _logger;
	private HttpListener? _listener;
	private CancellationTokenSource? _cts;
	private Task? _listenerTask;
	private CancellationTokenSource? _idleCts;
	private readonly object _lock = new();
	private string _sessionToken = GenerateSessionToken();

	public int Port { get; private set; } = 8194;
	public string BaseUrl => $"http://localhost:{Port}";

	private static string GenerateSessionToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

	public SoundboardManagerServer(DiscordIpcClient discordClient, ILogger logger)
	{
		_discordClient = discordClient;
		_logger = logger.ForContext<SoundboardManagerServer>();
	}

	public void EnsureStarted()
	{
		lock (_lock)
		{
			if (_listener != null && _listener.IsListening)
			{
				ResetIdleTimeout();
				return;
			}

			for (var p = 8194; p <= 8210; p++)
			{
				try
				{
					_listener = new HttpListener();
					_listener.Prefixes.Add($"http://127.0.0.1:{p}/");
					_listener.Prefixes.Add($"http://localhost:{p}/");
					_listener.Start();
					Port = p;
					_cts = new CancellationTokenSource();
					_listenerTask = Task.Run(() => ListenLoopAsync(_cts.Token), CancellationToken.None);
					_logger.Information("Soundboard Manager on-demand server running at {BaseUrl}", BaseUrl);
					ResetIdleTimeout();
					return;
				}
				catch (Exception ex)
				{
					_logger.Debug("Could not bind port {Port}: {Message}", p, ex.Message);
					try { _listener?.Close(); } catch { }
					_listener = null;
				}
			}

			_logger.Warning("Could not start Soundboard Manager on ports 8194-8210.");
		}
	}

	public void OpenWindow(string? callerWidgetId = null)
	{
		lock (_lock)
		{
			_sessionToken = GenerateSessionToken();
		}

		EnsureStarted();

		var url = $"{BaseUrl}/?token={_sessionToken}";
		var edgeLocations = new[]
		{
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
		};

		var edgePath = edgeLocations.FirstOrDefault(File.Exists);
		if (edgePath != null)
		{
			var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			var profileDir = Path.Combine(localAppData, "MacroDeck", "plugins", "com.sertaycil.discord-soundboard", "webview");
			try { Directory.CreateDirectory(profileDir); } catch { }

			try
			{
				var startInfo = new ProcessStartInfo
				{
					FileName = edgePath,
					Arguments = $"--app=\"{url}\" --window-size=1150,780 --user-data-dir=\"{profileDir}\"",
					UseShellExecute = false
				};
				Process.Start(startInfo);
				return;
			}
			catch (Exception ex)
			{
				_logger.Warning(ex, "Failed to launch Edge App Mode window, falling back to shell execute.");
			}
		}

		Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
	}

	public void Stop()
	{
		lock (_lock)
		{
			_idleCts?.Cancel();
			_idleCts = null;
			_cts?.Cancel();
			if (_listener != null)
			{
				try { _listener.Stop(); } catch { }
				try { _listener.Close(); } catch { }
				_listener = null;
			}
			_logger.Information("Soundboard Manager server stopped.");
		}
	}

	private void ResetIdleTimeout()
	{
		_idleCts?.Cancel();
		_idleCts = new CancellationTokenSource();
		var token = _idleCts.Token;

		_ = Task.Run(async () =>
		{
			try
			{
				await Task.Delay(TimeSpan.FromMinutes(10), token);
				if (!token.IsCancellationRequested)
				{
					_logger.Information("Soundboard Manager server idle timeout reached. Stopping server to release resources.");
					Stop();
				}
			}
			catch (OperationCanceledException) { }
		}, token);
	}

	private void ScheduleQuickShutdown()
	{
		_idleCts?.Cancel();
		_idleCts = new CancellationTokenSource();
		var token = _idleCts.Token;

		_ = Task.Run(async () =>
		{
			try
			{
				await Task.Delay(TimeSpan.FromSeconds(3), token);
				if (!token.IsCancellationRequested)
				{
					Stop();
				}
			}
			catch (OperationCanceledException) { }
		}, token);
	}

	private async Task ListenLoopAsync(CancellationToken ct)
	{
		while (!ct.IsCancellationRequested && _listener != null && _listener.IsListening)
		{
			try
			{
				var ctx = await _listener.GetContextAsync();
				_ = Task.Run(() => HandleRequestAsync(ctx), CancellationToken.None);
			}
			catch (HttpListenerException)
			{
				break;
			}
			catch (ObjectDisposedException)
			{
				break;
			}
			catch (Exception ex)
			{
				_logger.Debug(ex, "HttpListener error");
			}
		}
	}

	private async Task HandleRequestAsync(HttpListenerContext ctx)
	{
		ResetIdleTimeout();
		var req = ctx.Request;
		var resp = ctx.Response;

		// 1. Host header validation
		var host = req.Headers["Host"] ?? "";
		if (!host.StartsWith("127.0.0.1:", StringComparison.OrdinalIgnoreCase) &&
		    !host.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase))
		{
			resp.StatusCode = 403;
			resp.Close();
			return;
		}

		// 2. Origin / Referer validation (no cross-site requests allowed)
		var origin = req.Headers["Origin"];
		if (!string.IsNullOrEmpty(origin) && !origin.Equals($"http://{host}", StringComparison.OrdinalIgnoreCase))
		{
			resp.StatusCode = 403;
			resp.Close();
			return;
		}

		var referer = req.Headers["Referer"];
		if (!string.IsNullOrEmpty(referer) && !referer.StartsWith($"http://{host}/", StringComparison.OrdinalIgnoreCase))
		{
			resp.StatusCode = 403;
			resp.Close();
			return;
		}

		// No wildcard CORS: only echo back matching same-origin host
		if (!string.IsNullOrEmpty(origin) && origin.Equals($"http://{host}", StringComparison.OrdinalIgnoreCase))
		{
			resp.Headers.Add("Access-Control-Allow-Origin", origin);
			resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type, X-Session-Token");
			resp.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
		}

		if (req.HttpMethod == "OPTIONS")
		{
			resp.StatusCode = 204;
			resp.Close();
			return;
		}

		var path = req.Url?.AbsolutePath ?? "/";

		try
		{
			if (path == "/" || path == "/index.html")
			{
				await ServeDashboardHtmlAsync(resp, _sessionToken);
				return;
			}

			// Validate session token on all /api/ endpoints
			if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
			{
				var token = req.Headers["X-Session-Token"] ?? req.QueryString["token"];
				if (string.IsNullOrEmpty(token) || !string.Equals(token, _sessionToken, StringComparison.Ordinal))
				{
					resp.StatusCode = 401;
					await SendJsonResponseAsync(resp, "{\"error\":\"Unauthorized: Invalid session token\"}");
					return;
				}
			}

			if (path == "/api/status" && req.HttpMethod == "GET")
			{
				var json = JsonSerializer.Serialize(new
				{
					connected = _discordClient.IsConnected,
					authenticated = _discordClient.IsAuthenticated,
					username = _discordClient.CurrentUsername,
					soundCount = _discordClient.Sounds.Count
				});
				await SendJsonResponseAsync(resp, json);
				return;
			}

			if (path == "/api/sounds" && req.HttpMethod == "GET")
			{
				var json = JsonSerializer.Serialize(_discordClient.Sounds);
				await SendJsonResponseAsync(resp, json);
				return;
			}

			if (path == "/api/favorites" && req.HttpMethod == "GET")
			{
				var favs = LoadFavorites();
				await SendJsonResponseAsync(resp, JsonSerializer.Serialize(favs));
				return;
			}

			if (path == "/api/favorites" && req.HttpMethod == "POST")
			{
				using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
				var body = await reader.ReadToEndAsync();
				using var doc = JsonDocument.Parse(body);
				var soundId = doc.RootElement.TryGetProperty("soundId", out var sidProp) ? sidProp.GetString() : null;
				var isFav = doc.RootElement.TryGetProperty("isFavorite", out var favProp) && favProp.GetBoolean();

				if (!string.IsNullOrEmpty(soundId))
				{
					var favs = LoadFavorites();
					if (isFav) favs.Add(soundId);
					else favs.Remove(soundId);
					SaveFavorites(favs);
					await SendJsonResponseAsync(resp, JsonSerializer.Serialize(favs));
					return;
				}

				resp.StatusCode = 400;
				resp.Close();
				return;
			}

			// State-changing endpoint: Strictly POST
			if (path == "/api/play")
			{
				if (req.HttpMethod != "POST")
				{
					resp.StatusCode = 405;
					resp.Close();
					return;
				}

				using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
				var body = await reader.ReadToEndAsync();
				using var doc = JsonDocument.Parse(body);
				var soundId = doc.RootElement.TryGetProperty("soundId", out var sidProp) ? sidProp.GetString() : null;
				var guildId = doc.RootElement.TryGetProperty("guildId", out var gidProp) ? gidProp.GetString() : null;

				if (!string.IsNullOrEmpty(soundId))
				{
					var success = await _discordClient.PlaySoundAsync(soundId, guildId);
					await SendJsonResponseAsync(resp, JsonSerializer.Serialize(new { success }));
				}
				else
				{
					resp.StatusCode = 400;
					await SendJsonResponseAsync(resp, "{\"error\":\"Missing sound id\"}");
				}
				return;
			}

			if (path == "/api/close" && req.HttpMethod == "POST")
			{
				ScheduleQuickShutdown();
				await SendJsonResponseAsync(resp, "{\"success\":true}");
				return;
			}

			resp.StatusCode = 404;
			resp.Close();
		}
		catch (Exception ex)
		{
			_logger.Error(ex, "Error handling HTTP request for {Path}", path);
			resp.StatusCode = 500;
			resp.Close();
		}
	}

	private static async Task SendJsonResponseAsync(HttpListenerResponse resp, string json)
	{
		var bytes = Encoding.UTF8.GetBytes(json);
		resp.ContentType = "application/json; charset=utf-8";
		resp.ContentLength64 = bytes.Length;
		await resp.OutputStream.WriteAsync(bytes);
		resp.Close();
	}

	private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

	private static string GetFavoritesFilePath()
	{
		var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		var dir = Path.Combine(appData, "MacroDeck", "plugins", "com.sertaycil.discord-soundboard");
		Directory.CreateDirectory(dir);
		return Path.Combine(dir, "favorites.json");
	}

	private static HashSet<string> LoadFavorites()
	{
		try
		{
			var path = GetFavoritesFilePath();
			if (File.Exists(path))
			{
				var json = File.ReadAllText(path);
				var list = JsonSerializer.Deserialize<List<string>>(json);
				if (list != null) return new HashSet<string>(list);
			}
		}
		catch { }
		return [];
	}

	private static void SaveFavorites(HashSet<string> favorites)
	{
		try
		{
			var path = GetFavoritesFilePath();
			var json = JsonSerializer.Serialize(favorites, IndentedJsonOptions);
			File.WriteAllText(path, json);
		}
		catch { }
	}

	private static async Task ServeDashboardHtmlAsync(HttpListenerResponse resp, string sessionToken)
	{
		var safeToken = JavaScriptEncoder.Default.Encode(sessionToken);
		var html = GetDashboardHtml(safeToken);
		var bytes = Encoding.UTF8.GetBytes(html);
		resp.ContentType = "text/html; charset=utf-8";
		resp.ContentLength64 = bytes.Length;
		await resp.OutputStream.WriteAsync(bytes);
		resp.Close();
	}

	private static string GetDashboardHtml(string sessionToken) => $$"""
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Discord Soundboard - Macro Deck</title>
    <style>
        :root {
            --bg: #121315;
            --card-bg: #1e1f22;
            --card-hover: #2b2d31;
            --accent: #5865F2;
            --accent-hover: #4752c4;
            --text: #f2f3f5;
            --muted: #949ba4;
            --border: #313338;
            --success: #23a55a;
            --gold: #f0b232;
            --danger: #da373c;
        }
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body {
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
            background-color: var(--bg);
            color: var(--text);
            padding: 20px;
            user-select: none;
        }
        .container { max-width: 1240px; margin: 0 auto; }
        header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 16px;
            padding-bottom: 14px;
            border-bottom: 1px solid var(--border);
        }
        .title-area h1 { font-size: 22px; display: flex; align-items: center; gap: 10px; }
        .title-area p { color: var(--muted); font-size: 13px; margin-top: 3px; }
        .header-actions { display: flex; align-items: center; gap: 10px; }
        .status-badge {
            background: var(--card-bg);
            border: 1px solid var(--border);
            padding: 7px 12px;
            border-radius: 20px;
            font-size: 12px;
            display: flex;
            align-items: center;
            gap: 8px;
        }
        .status-dot { width: 9px; height: 9px; border-radius: 50%; background: var(--success); }
        .btn-header {
            background: var(--card-bg);
            border: 1px solid var(--border);
            color: var(--text);
            padding: 7px 12px;
            border-radius: 8px;
            cursor: pointer;
            font-size: 12px;
            display: flex;
            align-items: center;
            gap: 6px;
            transition: all 0.15s;
            outline: none;
        }
        .btn-header:hover { background: var(--card-hover); border-color: var(--accent); }

        /* Guide Banner */
        .guide-banner {
            background: linear-gradient(135deg, rgba(88, 101, 242, 0.15), rgba(35, 165, 90, 0.1));
            border: 1px solid rgba(88, 101, 242, 0.35);
            border-radius: 10px;
            padding: 12px 16px;
            margin-bottom: 16px;
            display: flex;
            align-items: center;
            gap: 12px;
            font-size: 13px;
            line-height: 1.45;
        }
        .guide-banner .guide-icon { font-size: 24px; flex-shrink: 0; }
        .guide-banner .guide-text strong { color: #fff; }
        
        /* Filter Bar */
        .filter-section {
            display: flex;
            gap: 10px;
            margin-bottom: 16px;
            align-items: center;
            flex-wrap: wrap;
        }
        .filter-chips {
            display: flex;
            gap: 8px;
            overflow-x: auto;
            padding-bottom: 4px;
            scrollbar-width: thin;
        }
        .filter-chip {
            background: var(--card-bg);
            border: 1px solid var(--border);
            color: var(--muted);
            padding: 7px 14px;
            border-radius: 18px;
            font-size: 13px;
            font-weight: 500;
            cursor: pointer;
            white-space: nowrap;
            display: inline-flex;
            align-items: center;
            gap: 6px;
            transition: all 0.18s;
        }
        .filter-chip:hover {
            background: var(--card-hover);
            color: var(--text);
            border-color: #4e5058;
        }
        .filter-chip.active {
            background: var(--accent);
            color: white;
            border-color: var(--accent);
        }
        .filter-chip .count-pill {
            background: rgba(0,0,0,0.25);
            padding: 2px 7px;
            border-radius: 10px;
            font-size: 11px;
        }
        .server-dropdown {
            background: var(--card-bg);
            border: 1px solid var(--border);
            color: var(--text);
            padding: 8px 12px;
            border-radius: 8px;
            font-size: 13px;
            outline: none;
            cursor: pointer;
            min-width: 220px;
        }
        .server-dropdown:focus { border-color: var(--accent); }

        .toolbar {
            display: flex;
            gap: 12px;
            margin-bottom: 18px;
            align-items: center;
        }
        .search-box {
            flex: 1;
            background: var(--card-bg);
            border: 1px solid var(--border);
            border-radius: 8px;
            padding: 9px 14px;
            color: var(--text);
            font-size: 14px;
            outline: none;
            transition: border-color 0.2s;
        }
        .search-box:focus { border-color: var(--accent); }

        .sound-grid {
            display: grid;
            grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
            gap: 14px;
        }
        .sound-card {
            background: var(--card-bg);
            border: 1px solid var(--border);
            border-radius: 10px;
            padding: 12px;
            display: flex;
            flex-direction: column;
            gap: 10px;
            transition: all 0.18s ease;
            position: relative;
        }
        .sound-card:hover {
            background: var(--card-hover);
            border-color: #4e5058;
            transform: translateY(-2px);
        }
        .card-top {
            display: flex;
            align-items: center;
            gap: 12px;
            position: relative;
        }
        .sound-icon {
            width: 44px;
            height: 44px;
            border-radius: 8px;
            background: #2b2d31;
            display: flex;
            align-items: center;
            justify-content: center;
            font-size: 24px;
            flex-shrink: 0;
            overflow: hidden;
        }
        .sound-icon img { width: 100%; height: 100%; object-fit: cover; }
        .sound-info { flex: 1; min-width: 0; padding-right: 24px; }
        .sound-name {
            font-weight: 600;
            font-size: 14px;
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
        }
        .sound-guild {
            font-size: 11px;
            color: var(--muted);
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
            margin-top: 2px;
        }
        .btn-fav {
            position: absolute;
            top: 0;
            right: 0;
            background: transparent;
            border: none;
            font-size: 18px;
            color: #4e5058;
            cursor: pointer;
            padding: 2px 4px;
            line-height: 1;
            transition: all 0.15s;
        }
        .btn-fav:hover { color: var(--gold); transform: scale(1.2); }
        .btn-fav.favorited { color: var(--gold); }
        
        .card-bottom {
            display: flex;
            gap: 6px;
            margin-top: auto;
            padding-top: 8px;
            border-top: 1px solid rgba(255,255,255,0.05);
        }
        .btn-action {
            background: transparent;
            border: 1px solid var(--border);
            color: var(--text);
            padding: 6px 8px;
            border-radius: 6px;
            font-size: 11px;
            font-weight: 500;
            cursor: pointer;
            display: flex;
            align-items: center;
            justify-content: center;
            gap: 4px;
            transition: all 0.15s;
            flex: 1;
            white-space: nowrap;
        }
        .btn-preview:hover { background: #3b3e45; border-color: #5865F2; color: #fff; }
        .btn-voice { background: rgba(88, 101, 242, 0.15); border-color: rgba(88, 101, 242, 0.4); color: #8ea1e1; }
        .btn-voice:hover { background: var(--accent); border-color: var(--accent); color: white; }
        .btn-copy:hover { background: #3b3e45; border-color: #949ba4; color: #fff; }

        .toast {
            position: fixed;
            bottom: 24px;
            right: 24px;
            background: var(--success);
            color: white;
            padding: 12px 20px;
            border-radius: 8px;
            font-size: 14px;
            font-weight: 500;
            box-shadow: 0 4px 12px rgba(0,0,0,0.5);
            transform: translateY(100px);
            opacity: 0;
            transition: all 0.3s cubic-bezier(0.18, 0.89, 0.32, 1.28);
            z-index: 1000;
        }
        .toast.show { transform: translateY(0); opacity: 1; }
    </style>
</head>
<body>
    <div class="container">
        <header>
            <div class="title-area">
                <h1 id="txtTitle">🎧 Discord Soundboard</h1>
                <p id="txtSubtitle">Explore and test all your Discord sounds with live preview.</p>
            </div>
            <div class="header-actions">
                <div class="status-badge">
                    <div class="status-dot" id="statusDot"></div>
                    <span id="statusText">Connecting...</span>
                </div>
                <button class="btn-header" id="langToggleBtn" onclick="toggleLanguage()">
                    🌐 <span id="langLabel">TR</span>
                </button>
                <button class="btn-header" id="btnClose" onclick="closeWindow()" title="Close">
                    ✕ <span id="txtClose">Close</span>
                </button>
            </div>
        </header>

        <div class="guide-banner">
            <div class="guide-icon">💡</div>
            <div class="guide-text" id="guideBannerText">
                <strong>Assigning Sounds to Macro Deck:</strong> In the Macro Deck app, click any button → <strong>Add Action</strong> → <strong>Discord Soundboard</strong> → Select <strong>Play Discord Soundboard Sound</strong> to pick your favorite sound with its official emoji icon!
            </div>
        </div>

        <div class="filter-section">
            <div class="filter-chips">
                <div class="filter-chip active" data-filter="all" onclick="setFilter('all')">
                    <span id="txtFilterAll">🌟 All</span> <span class="count-pill" id="countAll">0</span>
                </div>
                <div class="filter-chip" data-filter="favs" onclick="setFilter('favs')">
                    <span id="txtFilterFavs">⭐ Favorites</span> <span class="count-pill" id="countFavs">0</span>
                </div>
                <div class="filter-chip" data-filter="default" onclick="setFilter('default')">
                    <span id="txtFilterDefault">🔊 Discord Default</span> <span class="count-pill" id="countDefault">0</span>
                </div>
            </div>
            <select class="server-dropdown" id="serverSelect" onchange="onServerSelectChange(this.value)">
                <option value="">🏰 Filter by Server...</option>
            </select>
        </div>

        <div class="toolbar">
            <input type="text" class="search-box" id="searchInput" placeholder="🔍 Search by sound or server name..." oninput="onSearchInput(this.value)">
        </div>

        <div class="sound-grid" id="soundGrid">
            <div style="color: var(--muted); grid-column: 1 / -1; text-align: center; padding: 40px;" id="txtGridLoading">
                Sounds loading...
            </div>
        </div>
    </div>

    <div class="toast" id="toast">Notification</div>

    <script>
        window.__SESSION_TOKEN__ = "{{sessionToken}}";

        const i18n = {
            en: {
                title: "🎧 Discord Soundboard Manager",
                subtitle: "Explore, filter by server, preview audio, and test-play sounds live.",
                connecting: "Connecting...",
                waitingConnection: "Waiting for Connection",
                connectionError: "Server Connection Error",
                soundsLoaded: (user, count) => `${user || 'Discord'} (${count} sounds)`,
                closeBtn: "Close",
                allFilter: "🌟 All",
                favsFilter: "⭐ Favorites",
                defaultFilter: "🔊 Discord Default",
                serverPlaceholder: "🏰 Filter by Server...",
                searchPlaceholder: "🔍 Search by sound or server name...",
                guideText: `<strong>Assigning Sounds to Macro Deck:</strong> In Macro Deck, click any button → <strong>Add Action</strong> → <strong>Discord Soundboard</strong> → Select <strong>Play Discord Soundboard Sound</strong> to pick your favorite sound with its official emoji icon!`,
                emptySounds: "No matching sounds found.",
                loadingSounds: "Sounds loading...",
                soundsFetchError: "Could not retrieve sounds. Make sure Discord desktop app is running.",
                previewBtn: "▶ Preview",
                previewTooltip: "Listen to preview locally",
                playVoiceBtn: "📢 Play in Voice",
                playVoiceTooltip: "Play sound live in your active Discord voice channel",
                copyBtn: "📋 Copy ID",
                copyTooltip: "Copy Sound ID to clipboard",
                addFavTooltip: "Add to Favorites",
                removeFavTooltip: "Remove from Favorites",
                toastPlayed: (name) => `"${name}" played in voice channel! 🔊`,
                toastPlayError: "Could not play sound (are you connected to a Discord voice channel?)",
                toastPlayReqError: "Failed to send play request.",
                toastCopied: (id) => `Sound ID copied to clipboard: ${id}`,
                toastConnError: "Could not connect to server.",
                serverSoundCount: (name, count) => `${name} (${count} sounds)`,
                defaultGuildName: "Discord (Default)",
                serverFallback: (id) => `Server (${id})`
            },
            tr: {
                title: "🎧 Discord Soundboard Yöneticisi",
                subtitle: "Sunuculara göre filtreleyin, favorilerinizi belirleyin ve sesleri canlı dinleyin.",
                connecting: "Bağlanıyor...",
                waitingConnection: "Bağlantı Bekleniyor",
                connectionError: "Sunucu Bağlantı Hatası",
                soundsLoaded: (user, count) => `${user || 'Discord'} (${count} ses)`,
                closeBtn: "Kapat",
                allFilter: "🌟 Tümü",
                favsFilter: "⭐ Favorilerim",
                defaultFilter: "🔊 Discord Varsayılan",
                serverPlaceholder: "🏰 Belirli Bir Sunucu Seç...",
                searchPlaceholder: "🔍 Ses veya sunucu adına göre ara...",
                guideText: `<strong>Macro Deck Tuşuna Atama:</strong> Macro Deck uygulamasında istediğiniz tuşa tıklayın ➔ <strong>Action Ekle</strong> ➔ <strong>Discord Soundboard</strong> ➔ <strong>Play Discord Soundboard Sound</strong> seçerek bu sesi otomatik emojisiyle tuşunuza atayabilirsiniz!`,
                emptySounds: "Eşleşen ses bulunamadı.",
                loadingSounds: "Sesler yükleniyor...",
                soundsFetchError: "Sesler alınamadı. Discord uygulamasının açık olduğundan emin olun.",
                previewBtn: "▶ Önizle",
                previewTooltip: "Sesi tarayıcıda canlı dinle",
                playVoiceBtn: "📢 Seste Çal",
                playVoiceTooltip: "Sesi Discord ses kanalında canlı çal",
                copyBtn: "📋 ID Kopyala",
                copyTooltip: "Ses ID'sini panoya kopyala",
                addFavTooltip: "Favorilere Ekle",
                removeFavTooltip: "Favorilerden Çıkar",
                toastPlayed: (name) => `"${name}" ses kanalında çalındı! 🔊`,
                toastPlayError: "Ses çalınamadı (Discord'da ses kanalında mısınız?)",
                toastPlayReqError: "Çalma isteği iletilemedi.",
                toastCopied: (id) => `Ses ID panoya kopyalandı: ${id}`,
                toastConnError: "Sunucuya bağlanılamadı.",
                serverSoundCount: (name, count) => `${name} (${count} ses)`,
                defaultGuildName: "Discord (Varsayılan)",
                serverFallback: (id) => `Sunucu (${id})`
            }
        };

        let currentLang = localStorage.getItem('sfx_lang') || (navigator.language.startsWith('tr') ? 'tr' : 'en');
        function t(key, ...args) {
            const entry = i18n[currentLang][key];
            if (typeof entry === 'function') return entry(...args);
            return entry || key;
        }

        let sounds = [];
        let favorites = new Set();
        let lastStatus = null;
        let activeFilter = 'all';
        let activeGuildId = '';
        let searchQuery = '';
        let currentAudio = null;

        const grid = document.getElementById('soundGrid');
        const searchInput = document.getElementById('searchInput');
        const serverSelect = document.getElementById('serverSelect');
        const toast = document.getElementById('toast');
        const countAll = document.getElementById('countAll');
        const countFavs = document.getElementById('countFavs');
        const countDefault = document.getElementById('countDefault');

        function showToast(msg, bg = '#23a55a') {
            toast.textContent = msg;
            toast.style.background = bg;
            toast.classList.add('show');
            setTimeout(() => toast.classList.remove('show'), 3500);
        }

        async function apiFetch(url, options = {}) {
            const headers = Object.assign({}, options.headers, {
                'X-Session-Token': window.__SESSION_TOKEN__
            });
            if (options.body && typeof options.body === 'string') {
                headers['Content-Type'] = 'application/json';
            }
            return fetch(url, Object.assign({}, options, { headers }));
        }

        async function loadSoundsAndFavorites() {
            try {
                const stRes = await apiFetch('/api/status');
                lastStatus = await stRes.json();
                updateStatusBadge();

                const [soundsRes, favsRes] = await Promise.all([
                    apiFetch('/api/sounds'),
                    apiFetch('/api/favorites')
                ]);

                if (soundsRes.ok) sounds = await soundsRes.json();
                if (favsRes.ok) {
                    const favList = await favsRes.json();
                    favorites = new Set(favList);
                }

                updateFilterCounts();
                populateServerDropdown();
                renderGrid();
            } catch (e) {
                console.error(e);
                grid.innerHTML = '';
                const errDiv = document.createElement('div');
                errDiv.style.cssText = 'color: var(--danger); grid-column: 1 / -1; text-align: center; padding: 40px;';
                errDiv.textContent = t('soundsFetchError');
                grid.appendChild(errDiv);
            }
        }

        function updateStatusBadge() {
            const dot = document.getElementById('statusDot');
            const txt = document.getElementById('statusText');
            if (!lastStatus) {
                dot.style.background = 'var(--muted)';
                txt.textContent = t('waitingConnection');
            } else if (lastStatus.connected && lastStatus.authenticated) {
                dot.style.background = 'var(--success)';
                txt.textContent = t('soundsLoaded', lastStatus.username, lastStatus.soundCount);
            } else {
                dot.style.background = 'var(--danger)';
                txt.textContent = t('waitingConnection');
            }
        }

        function updateFilterCounts() {
            countAll.textContent = sounds.length;
            countFavs.textContent = sounds.filter(s => favorites.has(s.sound_id)).length;
            countDefault.textContent = sounds.filter(s => s.guild_id === '0' || !s.guild_id).length;
        }

        function getResolvedGuildName(s) {
            if (s.guild_name) return s.guild_name;
            if (s.guild_id === '0' || !s.guild_id) return t('defaultGuildName');
            return t('serverFallback', s.guild_id);
        }

        function populateServerDropdown() {
            const guildCounts = new Map();
            sounds.forEach(s => {
                if (s.guild_id && s.guild_id !== '0') {
                    const name = getResolvedGuildName(s);
                    guildCounts.set(s.guild_id, { name, count: (guildCounts.get(s.guild_id)?.count || 0) + 1 });
                }
            });

            serverSelect.innerHTML = '';
            const defaultOpt = document.createElement('option');
            defaultOpt.value = '';
            defaultOpt.textContent = t('serverPlaceholder');
            serverSelect.appendChild(defaultOpt);

            Array.from(guildCounts.entries())
                .sort((a, b) => a[1].name.localeCompare(b[1].name))
                .forEach(([gid, info]) => {
                    const opt = document.createElement('option');
                    opt.value = gid;
                    opt.textContent = t('serverSoundCount', info.name, info.count);
                    if (gid === activeGuildId) opt.selected = true;
                    serverSelect.appendChild(opt);
                });
        }

        function setFilter(type) {
            activeFilter = type;
            activeGuildId = '';
            serverSelect.value = '';
            document.querySelectorAll('.filter-chip').forEach(c => {
                c.classList.toggle('active', c.getAttribute('data-filter') === type);
            });
            renderGrid();
        }

        function onServerSelectChange(val) {
            activeGuildId = val;
            if (val) {
                activeFilter = 'guild';
                document.querySelectorAll('.filter-chip').forEach(c => c.classList.remove('active'));
            } else {
                setFilter('all');
            }
            renderGrid();
        }

        function onSearchInput(val) {
            searchQuery = (val || '').toLowerCase().trim();
            renderGrid();
        }

        function getFilteredSounds() {
            return sounds.filter(s => {
                if (activeFilter === 'favs' && !favorites.has(s.sound_id)) return false;
                if (activeFilter === 'default' && (s.guild_id !== '0' && s.guild_id)) return false;
                if (activeFilter === 'guild' && s.guild_id !== activeGuildId) return false;

                if (searchQuery) {
                    const nameMatch = (s.name || '').toLowerCase().includes(searchQuery);
                    const guildMatch = getResolvedGuildName(s).toLowerCase().includes(searchQuery);
                    if (!nameMatch && !guildMatch) return false;
                }
                return true;
            });
        }

        // Safe DOM building with zero innerHTML injection
        function renderGrid() {
            const filtered = getFilteredSounds();
            grid.innerHTML = '';

            if (filtered.length === 0) {
                const emptyDiv = document.createElement('div');
                emptyDiv.style.cssText = 'color: var(--muted); grid-column: 1 / -1; text-align: center; padding: 40px;';
                emptyDiv.textContent = t('emptySounds');
                grid.appendChild(emptyDiv);
                return;
            }

            filtered.forEach(s => {
                const card = document.createElement('div');
                card.className = 'sound-card';

                const cardTop = document.createElement('div');
                cardTop.className = 'card-top';

                const iconEl = document.createElement('div');
                iconEl.className = 'sound-icon';
                if (s.emoji_id && /^\d+$/.test(s.emoji_id)) {
                    const img = document.createElement('img');
                    img.src = `https://cdn.discordapp.com/emojis/${encodeURIComponent(s.emoji_id)}.png?size=96`;
                    img.alt = '';
                    img.loading = 'lazy';
                    iconEl.appendChild(img);
                } else if (s.emoji_name) {
                    iconEl.textContent = s.emoji_name;
                } else {
                    iconEl.textContent = '🔊';
                }

                const infoEl = document.createElement('div');
                infoEl.className = 'sound-info';

                const nameEl = document.createElement('div');
                nameEl.className = 'sound-name';
                nameEl.textContent = s.name || 'Sound';
                nameEl.title = s.name || '';

                const guildEl = document.createElement('div');
                guildEl.className = 'sound-guild';
                const displayGuild = getResolvedGuildName(s);
                guildEl.textContent = displayGuild;
                guildEl.title = displayGuild;

                infoEl.appendChild(nameEl);
                infoEl.appendChild(guildEl);

                const isFav = favorites.has(s.sound_id);
                const favBtn = document.createElement('button');
                favBtn.className = `btn-fav ${isFav ? 'favorited' : ''}`;
                favBtn.title = isFav ? t('removeFavTooltip') : t('addFavTooltip');
                favBtn.textContent = isFav ? '★' : '☆';

                favBtn.addEventListener('click', async (e) => {
                    e.stopPropagation();
                    const newFavState = !favorites.has(s.sound_id);
                    if (newFavState) favorites.add(s.sound_id);
                    else favorites.delete(s.sound_id);
                    favBtn.classList.toggle('favorited', newFavState);
                    favBtn.textContent = newFavState ? '★' : '☆';
                    updateFilterCounts();

                    try {
                        await apiFetch('/api/favorites', {
                            method: 'POST',
                            body: JSON.stringify({ soundId: s.sound_id, isFavorite: newFavState })
                        });
                    } catch (err) {
                        console.error('Failed to save favorite', err);
                    }
                });

                cardTop.appendChild(iconEl);
                cardTop.appendChild(infoEl);
                cardTop.appendChild(favBtn);

                const cardBottom = document.createElement('div');
                cardBottom.className = 'card-bottom';

                const playPreviewBtn = document.createElement('button');
                playPreviewBtn.className = 'btn-action btn-preview';
                playPreviewBtn.title = t('previewTooltip');
                playPreviewBtn.textContent = t('previewBtn');
                playPreviewBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    playPreviewAudio(s);
                });

                const playVoiceBtn = document.createElement('button');
                playVoiceBtn.className = 'btn-action btn-voice';
                playVoiceBtn.title = t('playVoiceTooltip');
                playVoiceBtn.textContent = t('playVoiceBtn');
                playVoiceBtn.addEventListener('click', async (e) => {
                    e.stopPropagation();
                    try {
                        const res = await apiFetch('/api/play', {
                            method: 'POST',
                            body: JSON.stringify({ soundId: s.sound_id, guildId: s.guild_id || null })
                        });
                        const data = await res.json();
                        if (data.success) {
                            showToast(t('toastPlayed', s.name));
                        } else {
                            showToast(t('toastPlayError'), '#da373c');
                        }
                    } catch (err) {
                        showToast(t('toastPlayReqError'), '#da373c');
                    }
                });

                const copyBtn = document.createElement('button');
                copyBtn.className = 'btn-action btn-copy';
                copyBtn.title = t('copyTooltip');
                copyBtn.textContent = t('copyBtn');
                copyBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    if (navigator.clipboard && navigator.clipboard.writeText) {
                        navigator.clipboard.writeText(s.sound_id);
                    }
                    showToast(t('toastCopied', s.name));
                });

                cardBottom.appendChild(playPreviewBtn);
                cardBottom.appendChild(playVoiceBtn);
                cardBottom.appendChild(copyBtn);

                card.appendChild(cardTop);
                card.appendChild(cardBottom);
                grid.appendChild(card);
            });
        }

        function playPreviewAudio(s) {
            try {
                if (currentAudio) {
                    currentAudio.pause();
                    currentAudio.currentTime = 0;
                }
                const url = `https://cdn.discordapp.com/soundboard-sounds/${encodeURIComponent(s.sound_id)}`;
                currentAudio = new Audio(url);
                currentAudio.volume = 0.8;
                currentAudio.play().catch(e => {
                    console.warn('Audio preview error', e);
                    showToast(t('toastPlayReqError'), '#da373c');
                });
            } catch (err) {
                console.error(err);
            }
        }

        async function closeWindow() {
            try {
                await apiFetch('/api/close', { method: 'POST' });
            } catch (e) { }
            window.close();
        }

        function applyI18n() {
            document.getElementById('txtTitle').textContent = t('title');
            document.getElementById('txtSubtitle').textContent = t('subtitle');
            document.getElementById('txtClose').textContent = t('closeBtn');
            document.getElementById('langLabel').textContent = currentLang === 'tr' ? 'EN' : 'TR';
            document.getElementById('guideBannerText').innerHTML = t('guideText');
            document.getElementById('txtFilterAll').textContent = t('allFilter');
            document.getElementById('txtFilterFavs').textContent = t('favsFilter');
            document.getElementById('txtFilterDefault').textContent = t('defaultFilter');
            document.getElementById('searchInput').placeholder = t('searchPlaceholder');

            updateStatusBadge();
            populateServerDropdown();
            renderGrid();
        }

        function toggleLanguage() {
            currentLang = currentLang === 'tr' ? 'en' : 'tr';
            localStorage.setItem('sfx_lang', currentLang);
            applyI18n();
        }

        // Initialize on load
        applyI18n();
        loadSoundsAndFavorites();
    </script>
</body>
</html>
""";

	public async ValueTask DisposeAsync()
	{
		Stop();
		if (_listenerTask != null)
		{
			try { await _listenerTask; } catch { }
		}
	}
}

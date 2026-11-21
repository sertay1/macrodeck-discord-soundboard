using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

	public int Port { get; private set; } = 8194;
	public string BaseUrl => $"http://localhost:{Port}";
	public string? LastCallerWidgetId { get; set; }

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
		if (!string.IsNullOrEmpty(callerWidgetId))
		{
			LastCallerWidgetId = callerWidgetId;
		}

		EnsureStarted();

		var url = BaseUrl;
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
				await Task.Delay(TimeSpan.FromSeconds(5), token);
				if (!token.IsCancellationRequested)
				{
					_logger.Information("Manager window closed. Stopping on-demand server.");
					Stop();
				}
			}
			catch (OperationCanceledException) { }
		}, token);
	}

	private async Task ListenLoopAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested && _listener?.IsListening == true)
		{
			try
			{
				var ctx = await _listener.GetContextAsync();
				_ = Task.Run(() => HandleRequestAsync(ctx), CancellationToken.None);
			}
			catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
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

		resp.Headers.Add("Access-Control-Allow-Origin", "*");
		resp.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
		resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

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
				await ServeDashboardHtmlAsync(resp);
				return;
			}

			if (path == "/api/status")
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

			if (path == "/api/sounds")
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

			if (path == "/api/play")
			{
				var soundId = req.QueryString["id"] ?? "";
				var guildId = req.QueryString["guild_id"];
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

			if (path == "/api/folders" && req.HttpMethod == "GET")
			{
				var folderList = GetProfileFolders();
				await SendJsonResponseAsync(resp, JsonSerializer.Serialize(folderList));
				return;
			}

			if (path == "/api/sync" && req.HttpMethod == "POST")
			{
				using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
				var body = await reader.ReadToEndAsync();
				using var doc = JsonDocument.Parse(body);
				var soundIds = new List<string>();
				if (doc.RootElement.TryGetProperty("soundIds", out var arr) && arr.ValueKind == JsonValueKind.Array)
				{
					foreach (var item in arr.EnumerateArray())
					{
						var s = item.GetString();
						if (!string.IsNullOrEmpty(s)) soundIds.Add(s);
					}
				}

				string? requestedFolderId = null;
				if (doc.RootElement.TryGetProperty("folderId", out var fIdProp))
				{
					requestedFolderId = fIdProp.GetString();
				}

				var (count, folderName) = SyncToMacroDeckProfile(soundIds, requestedFolderId);
				await SendJsonResponseAsync(resp, JsonSerializer.Serialize(new
				{
					success = true,
					count,
					folderName,
					needsRestart = true
				}));
				return;
			}

			if (path == "/api/restart-macrodeck" && req.HttpMethod == "POST")
			{
				_logger.Information("Macro Deck restart requested from Web UI.");
				RestartMacroDeck();
				await SendJsonResponseAsync(resp, "{\"success\":true}");
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

	private sealed record ProfileFolderDto(string id, string name, bool isCaller);

	private List<ProfileFolderDto> GetProfileFolders()
	{
		var result = new List<ProfileFolderDto>();
		try
		{
			var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			var profilesDir = Path.Combine(appData, "MacroDeck", "data", "profiles");
			if (!Directory.Exists(profilesDir)) return result;

			var profileFiles = Directory.GetFiles(profilesDir, "*.json")
				.Where(f => !f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) && !f.Contains(".bak_"))
				.ToList();
			if (profileFiles.Count == 0) return result;

			var targetProfilePath = profileFiles.OrderByDescending(f => File.GetLastWriteTime(f)).First();
			var profileJson = File.ReadAllText(targetProfilePath);

			var node = JsonNode.Parse(profileJson);
			if (node?["folders"] is JsonArray folders)
			{
				string? callerFolderId = null;
				if (!string.IsNullOrEmpty(LastCallerWidgetId))
				{
					foreach (var f in folders)
					{
						if (f is not JsonObject fObj) continue;
						if (fObj["widgets"] is JsonArray widgets)
						{
							foreach (var w in widgets)
							{
								if (w is JsonObject wObj && string.Equals(wObj["id"]?.ToString(), LastCallerWidgetId, StringComparison.OrdinalIgnoreCase))
								{
									callerFolderId = fObj["id"]?.ToString();
									break;
								}
							}
						}
						if (callerFolderId != null) break;
					}
				}

				foreach (var f in folders)
				{
					if (f is not JsonObject fObj) continue;
					var id = fObj["id"]?.ToString() ?? "";
					var name = fObj["name"]?.ToString() ?? "";
					var isCaller = callerFolderId != null && string.Equals(id, callerFolderId, StringComparison.OrdinalIgnoreCase);
					result.Add(new ProfileFolderDto(id, name, isCaller));
				}
			}
		}
		catch (Exception ex)
		{
			_logger.Warning(ex, "Failed to retrieve profile folders.");
		}
		return result;
	}

	private (int count, string folderName) SyncToMacroDeckProfile(IReadOnlyList<string> soundIds, string? requestedFolderId = null)
	{
		try
		{
			var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			var profilesDir = Path.Combine(appData, "MacroDeck", "data", "profiles");
			if (!Directory.Exists(profilesDir)) return (0, "Discord");

			var profileFiles = Directory.GetFiles(profilesDir, "*.json")
				.Where(f => !f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) && !f.Contains(".bak_"))
				.ToList();
			if (profileFiles.Count == 0) return (0, "Discord");

			var targetProfilePath = profileFiles.OrderByDescending(f => File.GetLastWriteTime(f)).First();
			var profileJson = File.ReadAllText(targetProfilePath);

			var node = JsonNode.Parse(profileJson);
			if (node == null) return (0, "Discord");

			var folders = node["folders"] as JsonArray;
			if (folders == null)
			{
				folders = [];
				node["folders"] = folders;
			}

			JsonObject? targetFolder = null;
			string? homeFolderId = null;

			// First, resolve homeFolderId and check if requestedFolderId matches
			foreach (var item in folders)
			{
				if (item is not JsonObject fObj) continue;
				var fid = fObj["id"]?.ToString();
				var fname = fObj["name"]?.ToString();

				if (string.Equals(fname, "Home", StringComparison.OrdinalIgnoreCase))
				{
					homeFolderId = fid;
				}

				if (!string.IsNullOrEmpty(requestedFolderId) && string.Equals(fid, requestedFolderId, StringComparison.OrdinalIgnoreCase))
				{
					targetFolder = fObj;
				}
			}

			// If no explicit folder requested or not found, try finding by caller widget id
			if (targetFolder == null && !string.IsNullOrEmpty(LastCallerWidgetId))
			{
				foreach (var item in folders)
				{
					if (item is not JsonObject fObj) continue;
					if (fObj["widgets"] is JsonArray widgets)
					{
						foreach (var w in widgets)
						{
							if (w is JsonObject wObj && string.Equals(wObj["id"]?.ToString(), LastCallerWidgetId, StringComparison.OrdinalIgnoreCase))
							{
								targetFolder = fObj;
								break;
							}
						}
					}
					if (targetFolder != null) break;
				}
			}

			// If still null, try finding "Ses Paneli", then "Discord"
			if (targetFolder == null)
			{
				foreach (var item in folders)
				{
					if (item is not JsonObject fObj) continue;
					var name = fObj["name"]?.ToString();
					if (string.Equals(name, "Ses Paneli", StringComparison.OrdinalIgnoreCase) ||
					    string.Equals(name, "Discord", StringComparison.OrdinalIgnoreCase) ||
					    string.Equals(name, "Discord Sounds", StringComparison.OrdinalIgnoreCase))
					{
						targetFolder = fObj;
						break;
					}
				}
			}

			// If still null, create a Discord folder
			if (targetFolder == null)
			{
				var newFolderId = Guid.NewGuid().ToString();
				targetFolder = new JsonObject
				{
					["id"] = newFolderId,
					["name"] = "Discord",
					["parentId"] = homeFolderId ?? string.Empty,
					["order"] = folders.Count,
					["rows"] = 4,
					["columns"] = 8,
					["backgroundColor"] = "#0b0e14",
					["widgetSpacing"] = 16,
					["widgetBorderRadius"] = 28,
					["createdAt"] = DateTime.UtcNow.ToString("o"),
					["widgets"] = new JsonArray()
				};
				folders.Add(targetFolder);
			}

			var targetFolderName = targetFolder["name"]?.ToString() ?? "Ses Paneli";

			var widgetsArray = targetFolder["widgets"] as JsonArray;
			if (widgetsArray == null)
			{
				widgetsArray = [];
				targetFolder["widgets"] = widgetsArray;
			}

			var columns = targetFolder["columns"]?.GetValue<int>() ?? 8;
			var rows = targetFolder["rows"]?.GetValue<int>() ?? 4;
			if (columns <= 0) columns = 8;
			if (rows <= 0) rows = 4;

			var occupied = new HashSet<(int x, int y)>();
			var existingSoundIds = new HashSet<string>();
			var hasBackNavigation = false;

			foreach (var w in widgetsArray)
			{
				if (w is not JsonObject wObj) continue;
				var posX = wObj["positionX"]?.GetValue<int>() ?? -1;
				var posY = wObj["positionY"]?.GetValue<int>() ?? -1;
				if (posX >= 0 && posY >= 0)
				{
					occupied.Add((posX, posY));
				}

				var dataStr = wObj["data"]?.ToString() ?? "";
				if (dataStr.Contains("go-to-parent") || dataStr.Contains("navigate-to-folder"))
				{
					hasBackNavigation = true;
				}

				foreach (var sid in soundIds)
				{
					if (dataStr.Contains($"\"value\":\"{sid}\""))
					{
						existingSoundIds.Add(sid);
					}
				}
			}

			// Only add back button if it's not the Home folder, has no back nav, and (0,0) is FREE
			if (!hasBackNavigation && !occupied.Contains((0, 0)) && !string.Equals(targetFolderName, "Home", StringComparison.OrdinalIgnoreCase))
			{
				widgetsArray.Add(CreateBackButton(0, 0));
				occupied.Add((0, 0));
			}

			var addedCount = 0;
			var currentX = 0;
			var currentY = 0;

			foreach (var sid in soundIds)
			{
				if (existingSoundIds.Contains(sid)) continue;

				var sound = _discordClient.Sounds.FirstOrDefault(s => s.SoundId == sid);
				if (sound == null) continue;

				while (occupied.Contains((currentX, currentY)))
				{
					currentX++;
					if (currentX >= columns)
					{
						currentX = 0;
						currentY++;
					}
				}

				if (currentY >= rows)
				{
					rows = currentY + 1;
					targetFolder["rows"] = rows;
				}

				widgetsArray.Add(CreateNativeSoundButton(sound, currentX, currentY));
				occupied.Add((currentX, currentY));
				addedCount++;

				_ = Task.Run(async () =>
				{
					try
					{
						if (OperatingSystem.IsWindows())
						{
							await IconHelper.GetIconPngBytesAsync(sound, _discordClient.HttpClient, CancellationToken.None);
						}
					}
					catch { }
				});

				currentX++;
				if (currentX >= columns)
				{
					currentX = 0;
					currentY++;
				}
			}

			try
			{
				File.Copy(targetProfilePath, targetProfilePath + ".bak", overwrite: true);
			}
			catch { }

			File.WriteAllText(targetProfilePath, node.ToJsonString(IndentedJsonOptions));
			_logger.Information("Synced {Count} native buttons to Macro Deck folder '{Folder}'", addedCount, targetFolderName);
			return (addedCount, targetFolderName);
		}
		catch (Exception ex)
		{
			_logger.Error(ex, "Failed to sync buttons to Macro Deck profile.");
			return (0, "Discord");
		}
	}

	private static JsonObject CreateNativeSoundButton(DiscordSound sound, int posX, int posY)
	{
		var label = sound.Name;
		var actionChildId = Guid.NewGuid().ToString();

		var flowsJson = JsonSerializer.Serialize(new[]
		{
			new
			{
				triggerId = "onShortPress",
				triggerType = "onShortPress",
				triggerLabel = "Short press",
				children = new[]
				{
					new
					{
						id = actionChildId,
						type = "action",
						blockType = "com.sertaycil.discord-soundboard.play-sound",
						label = "Play Sound",
						color = "#3b82f6",
						integrationId = "com.sertaycil.discord-soundboard",
						actionId = "play-sound",
						parameters = new[]
						{
							new
							{
								name = "soundId",
								type = "dynamic-choice",
								label = "Sound",
								description = "Select the Discord soundboard sound to play.",
								placeholder = "Select a sound...",
								autoPrefixHttps = false,
								defaultValue = (string?)null,
								required = true,
								multiline = false,
								supportsReset = false,
								literalOnly = false,
								validationRegex = (string?)null,
								maxLength = (int?)null,
								min = (int?)null,
								max = (int?)null,
								step = (int?)null,
								showSlider = false,
								allowAlpha = (bool?)null,
								searchable = (bool?)null,
								dynamicOptions = true,
								optionsSourceId = (string?)null,
								allowSelf = true,
								widgetTypes = Array.Empty<string>(),
								fileExtensions = (string?)null,
								language = (string?)null,
								variableInsertFormat = (string?)null,
								value = sound.SoundId,
								valueLabel = sound.Name
							}
						}
					}
				}
			}
		});

		var widgetData = new
		{
			label,
			flows = flowsJson,
			stateMode = false,
			fontSize = 14,
			textAlign = "center",
			labelPosition = "center",
			labelShadow = true,
			labelOutlineWidth = 1,
			labelBoxBorderWidth = 1,
			border = new { style = "off" },
			iconProvider = new
			{
				blockId = actionChildId,
				integrationId = "com.sertaycil.discord-soundboard",
				actionId = "play-sound",
				actionLabel = "Play Sound"
			}
		};

		return new JsonObject
		{
			["id"] = Guid.NewGuid().ToString(),
			["type"] = "ActionButton",
			["positionX"] = posX,
			["positionY"] = posY,
			["width"] = 1,
			["height"] = 1,
			["data"] = JsonSerializer.Serialize(widgetData),
			["isPinned"] = false
		};
	}

	private static JsonObject CreateBackButton(int posX, int posY)
	{
		var actionChildId = Guid.NewGuid().ToString();
		var flowsJson = JsonSerializer.Serialize(new[]
		{
			new
			{
				triggerId = "onShortPress",
				triggerType = "onShortPress",
				triggerLabel = "Short press",
				children = new[]
				{
					new
					{
						id = actionChildId,
						type = "action",
						blockType = "app.macro-deck.deck.go-to-parent",
						label = "Go to parent",
						color = "#3b82f6",
						integrationId = "app.macro-deck.deck",
						actionId = "go-to-parent",
						parameters = Array.Empty<object>()
					}
				}
			}
		});

		var widgetData = new
		{
			label = "\n",
			flows = flowsJson,
			stateMode = false,
			backgroundColor = "#0e1219",
			border = new { style = "off" },
			iconDisplay = new
			{
				fit = "contain",
				zoom = 100,
				offsetX = 0,
				offsetY = 0,
				opacity = 100
			},
			fontSize = 14,
			textAlign = "center",
			labelPosition = "center",
			labelShadow = true,
			labelOutlineWidth = 1,
			labelBoxBorderWidth = 1,
			icon = new
			{
				type = "icon-pack",
				reference = "01a1adc5-3e7e-7b41-aaa0-ee946a9caa68"
			}
		};

		return new JsonObject
		{
			["id"] = Guid.NewGuid().ToString(),
			["type"] = "ActionButton",
			["positionX"] = posX,
			["positionY"] = posY,
			["width"] = 1,
			["height"] = 1,
			["data"] = JsonSerializer.Serialize(widgetData),
			["isPinned"] = false
		};
	}

	public static void RestartMacroDeck()
	{
		var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		var exePath = Path.Combine(localAppData, "Macro Deck", "MacroDeck.exe");
		if (!File.Exists(exePath)) return;

		try
		{
			var script = $"Start-Sleep -Milliseconds 600; Stop-Process -Name 'MacroDeck','MacroDeckHost' -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 1200; Start-Process -FilePath '{exePath}'";
			var psi = new ProcessStartInfo
			{
				FileName = "powershell.exe",
				Arguments = $"-NoProfile -WindowStyle Hidden -Command \"{script}\"",
				UseShellExecute = false,
				CreateNoWindow = true
			};
			Process.Start(psi);
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Failed to trigger Macro Deck restart script.");
		}
	}

	private static async Task ServeDashboardHtmlAsync(HttpListenerResponse resp)
	{
		const string html = """
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
		            margin-bottom: 18px;
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
		            min-width: 200px;
		        }
		        .server-dropdown:focus { border-color: var(--accent); }

		        .toolbar {
		            display: flex;
		            gap: 12px;
		            margin-bottom: 20px;
		            align-items: center;
		            flex-wrap: wrap;
		        }
		        .search-box {
		            flex: 1;
		            min-width: 260px;
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
		        .btn {
		            padding: 9px 16px;
		            border-radius: 8px;
		            font-size: 13px;
		            font-weight: 500;
		            cursor: pointer;
		            border: none;
		            transition: all 0.2s;
		            display: inline-flex;
		            align-items: center;
		            gap: 8px;
		        }
		        .btn-primary { background: var(--accent); color: white; }
		        .btn-primary:hover { background: var(--accent-hover); }
		        .btn-secondary { background: var(--card-bg); color: var(--text); border: 1px solid var(--border); }
		        .btn-secondary:hover { background: var(--card-hover); }
		        .btn:disabled { opacity: 0.5; cursor: not-allowed; }
		        
		        .sound-grid {
		            display: grid;
		            grid-template-columns: repeat(auto-fill, minmax(210px, 1fr));
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
		            cursor: pointer;
		        }
		        .sound-card:hover {
		            background: var(--card-hover);
		            border-color: #4e5058;
		            transform: translateY(-2px);
		        }
		        .sound-card.selected {
		            border-color: var(--accent);
		            background: rgba(88, 101, 242, 0.12);
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
		        .sound-info { flex: 1; min-width: 0; padding-right: 22px; }
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
		            justify-content: space-between;
		            align-items: center;
		            margin-top: auto;
		            padding-top: 8px;
		            border-top: 1px solid rgba(255,255,255,0.05);
		        }
		        .btn-play {
		            background: transparent;
		            border: 1px solid var(--border);
		            color: var(--text);
		            padding: 5px 10px;
		            border-radius: 6px;
		            font-size: 12px;
		            cursor: pointer;
		            display: flex;
		            align-items: center;
		            gap: 5px;
		            transition: all 0.15s;
		        }
		        .btn-play:hover { background: var(--accent); border-color: var(--accent); color: white; }
		        .checkbox-custom {
		            width: 18px;
		            height: 18px;
		            border-radius: 4px;
		            border: 2px solid var(--muted);
		            display: flex;
		            align-items: center;
		            justify-content: center;
		            transition: all 0.15s;
		        }
		        .sound-card.selected .checkbox-custom {
		            background: var(--accent);
		            border-color: var(--accent);
		        }
		        .sound-card.selected .checkbox-custom::after {
		            content: '✓';
		            color: white;
		            font-size: 12px;
		            font-weight: bold;
		        }
		        .toast {
		            position: fixed;
		            bottom: 24px;
		            right: 24px;
		            background: #23a55a;
		            color: white;
		            padding: 12px 20px;
		            border-radius: 8px;
		            font-weight: 500;
		            font-size: 14px;
		            box-shadow: 0 4px 14px rgba(0,0,0,0.4);
		            transform: translateY(100px);
		            opacity: 0;
		            transition: all 0.3s cubic-bezier(0.18, 0.89, 0.32, 1.28);
		            z-index: 1000;
		        }
		        .toast.show { transform: translateY(0); opacity: 1; }
		        /* Modal */
		        .modal-overlay {
		            position: fixed;
		            inset: 0;
		            background: rgba(0, 0, 0, 0.7);
		            display: flex;
		            align-items: center;
		            justify-content: center;
		            z-index: 2000;
		            opacity: 0;
		            pointer-events: none;
		            transition: opacity 0.2s ease;
		        }
		        .modal-overlay.active {
		            opacity: 1;
		            pointer-events: auto;
		        }
		        .modal {
		            background: var(--card-bg);
		            border: 1px solid var(--border);
		            border-radius: 12px;
		            padding: 24px;
		            max-width: 440px;
		            width: 90%;
		            box-shadow: 0 10px 30px rgba(0,0,0,0.6);
		            text-align: center;
		        }
		        .modal h2 { font-size: 20px; margin-bottom: 12px; display: flex; align-items: center; justify-content: center; gap: 8px; }
		        .modal p { color: var(--muted); font-size: 14px; line-height: 1.5; margin-bottom: 20px; }
		        .modal-buttons { display: flex; flex-direction: column; gap: 10px; }
		        .modal-buttons button { width: 100%; justify-content: center; padding: 11px; }
		    </style>
		</head>
		<body>
		    <div class="container">
		        <header>
		            <div class="title-area">
		                <h1>
		                    <svg width="24" height="24" viewBox="0 0 24 24" fill="#5865F2"><path d="M20.317 4.37a19.791 19.791 0 0 0-4.885-1.515.074.074 0 0 0-.079.037c-.21.375-.444.864-.608 1.25a18.27 18.27 0 0 0-5.487 0 12.64 12.64 0 0 0-.617-1.25.077.077 0 0 0-.079-.037A19.736 19.736 0 0 0 3.677 4.37a.07.07 0 0 0-.032.027C.533 9.046-.32 13.58.099 18.057a.082.082 0 0 0 .031.057 19.9 19.9 0 0 0 5.993 3.03.078.078 0 0 0 .084-.028c.462-.63.874-1.295 1.226-1.994.021-.041.001-.09-.041-.106a13.107 13.107 0 0 1-1.872-.892.077.077 0 0 1-.008-.128 10.2 10.2 0 0 0 .372-.292.074.074 0 0 1 .077-.01c3.929 1.793 8.18 1.793 12.061 0a.074.074 0 0 1 .078.01c.12.098.246.198.373.292a.077.077 0 0 1-.006.127 12.299 12.299 0 0 1-1.873.893.077.077 0 0 0-.041.107c.36.698.772 1.362 1.225 1.993a.076.076 0 0 0 .084.028 19.839 19.839 0 0 0 6.002-3.03.077.077 0 0 0 .032-.054c.5-5.177-.838-9.674-3.549-13.66a.061.061 0 0 0-.031-.028zM8.02 15.33c-1.183 0-2.157-1.085-2.157-2.419 0-1.333.956-2.419 2.157-2.419 1.21 0 2.176 1.096 2.157 2.42 0 1.333-.956 2.418-2.157 2.418zm7.975 0c-1.183 0-2.157-1.085-2.157-2.419 0-1.333.955-2.419 2.157-2.419 1.21 0 2.176 1.096 2.157 2.42 0 1.333-.946 2.418-2.157 2.418z"/></svg>
		                    <span id="txtHeaderTitle">Discord Soundboard Manager</span>
		                </h1>
		                <p id="txtHeaderSubtitle">Filter by server, pick your favorites, and export directly as Macro Deck buttons.</p>
		            </div>
		            <div class="header-actions">
		                <select class="btn-header" id="langSelect" onchange="changeLanguage(this.value)" style="cursor:pointer; font-weight:600; padding:6px 10px;">
		                    <option value="en">🌐 English</option>
		                    <option value="tr">🌐 Türkçe</option>
		                </select>
		                <div class="status-badge" id="statusBadge">
		                    <div class="status-dot"></div>
		                    <span id="statusText">Connecting...</span>
		                </div>
		                <button class="btn-header" id="btnRestart" onclick="restartMacroDeck()" title="Restart Macro Deck">🔄 Restart</button>
		                <button class="btn-header" id="btnClose" onclick="window.close()" title="Close Window">✕ Close</button>
		            </div>
		        </header>

		        <!-- Server & Category Filter Bar -->
		        <div class="filter-section">
		            <div class="filter-chips" id="filterChips">
		                <div class="filter-chip active" data-filter="all"><span id="txtFilterAll">🌟 All</span> <span class="count-pill" id="countAll">0</span></div>
		                <div class="filter-chip" data-filter="favorites"><span id="txtFilterFavs">⭐ Favorites</span> <span class="count-pill" id="countFavs">0</span></div>
		                <div class="filter-chip" data-filter="0"><span id="txtFilterDefault">🔊 Discord Default</span> <span class="count-pill" id="countDefault">0</span></div>
		            </div>
		            <select class="server-dropdown" id="serverSelect">
		                <option value="">🏰 Filter by Server...</option>
		            </select>
		        </div>

		        <div class="toolbar">
		            <input type="text" class="search-box" id="searchInput" placeholder="🔍 Search by sound or server name...">
		            <div style="display: flex; align-items: center; gap: 8px;">
		                <label for="targetFolderSelect" id="lblTargetFolder" style="font-size: 13px; color: var(--muted); white-space: nowrap;">📁 Target Folder:</label>
		                <select class="server-dropdown" id="targetFolderSelect" style="min-width: 180px;">
		                    <option value="">Loading...</option>
		                </select>
		            </div>
		            <button class="btn btn-secondary" id="btnSelectAll">Select / Clear Visible</button>
		            <button class="btn btn-primary" id="btnSync" disabled>
		                🚀 Export Selected to Macro Deck (<span id="selCount">0</span>)
		            </button>
		        </div>

		        <div class="sound-grid" id="soundGrid">
		            <div style="color: var(--muted); grid-column: 1 / -1; text-align: center; padding: 40px;" id="txtGridLoading">
		                Sounds loading...
		            </div>
		        </div>
		    </div>

		    <div class="toast" id="toast">Notification</div>

		    <!-- Success Modal -->
		    <div class="modal-overlay" id="successModal">
		        <div class="modal">
		            <h2 id="modalTitle">🎉 Sounds Exported!</h2>
		            <p id="modalMessage">Your selected sounds have been added as buttons to Macro Deck.</p>
		            <div class="modal-buttons">
		                <button class="btn btn-primary" id="modalBtnRestart" onclick="restartMacroDeckNow()">
		                    🔄 Restart Macro Deck (Apply Now)
		                </button>
		                <button class="btn btn-secondary" id="modalBtnLater" onclick="closeModal()">
		                    Done (Restart Later)
		                </button>
		            </div>
		        </div>
		    </div>

		    <script>
		        const i18n = {
		            en: {
		                title: "Discord Soundboard Manager",
		                subtitle: "Filter by server, pick your favorites, and export directly as Macro Deck buttons.",
		                connecting: "Connecting...",
		                waitingConnection: "Waiting for Connection",
		                connectionError: "Server Connection Error",
		                soundsLoaded: (user, count) => `${user || 'Discord'} (${count} sounds)`,
		                restartBtn: "🔄 Restart",
		                restartTitle: "Restart Macro Deck",
		                closeBtn: "✕ Close",
		                closeTitle: "Close Window",
		                allFilter: "🌟 All",
		                favsFilter: "⭐ Favorites",
		                defaultFilter: "🔊 Discord Default",
		                serverPlaceholder: "🏰 Filter by Server...",
		                searchPlaceholder: "🔍 Search by sound or server name...",
		                targetFolderLabel: "📁 Target Folder:",
		                loading: "Loading...",
		                defaultFolder: "(Default Folder)",
		                currentFolderSuffix: " (Current Folder)",
		                selectAllBtn: "Select / Clear Visible",
		                syncBtn: (count) => `🚀 Export Selected to Macro Deck (<span id="selCount">${count}</span>)`,
		                syncingBtn: "Exporting...",
		                emptySounds: "No matching sounds found.",
		                loadingSounds: "Sounds loading...",
		                soundsFetchError: "Could not retrieve sounds. Make sure Discord desktop app is running.",
		                playBtn: "▶ Play",
		                playTooltip: "Preview Play",
		                addFavTooltip: "Add to Favorites",
		                removeFavTooltip: "Remove from Favorites",
		                toastPlayed: (name) => `"${name}" played successfully! 🔊`,
		                toastPlayError: "Could not play sound (are you connected to a voice channel?)",
		                toastPlayReqError: "Failed to send play request.",
		                toastNoSelection: "Please select sounds to export first!",
		                toastSyncError: "Error occurred during export.",
		                toastConnError: "Could not connect to server.",
		                toastRestarting: "Macro Deck restarting in 1-2 seconds...",
		                restartConfirm: "Restart Macro Deck now?",
		                modalTitle: "🎉 Sounds Exported!",
		                modalMsg: (count, folder) => `<strong>${count} sounds</strong> successfully added as buttons to Macro Deck folder "<strong>${folder}</strong>" with generated icons.<br><br>Restart Macro Deck now to display your new buttons immediately:`,
		                modalRestartBtn: "🔄 Restart Macro Deck (Apply Now)",
		                modalLaterBtn: "Done (Restart Later)",
		                serverSoundCount: (name, count) => `${name} (${count} sounds)`,
		                defaultGuildName: "Discord (Default)",
		                serverFallback: (id) => `Server (${id})`
		            },
		            tr: {
		                title: "Discord Soundboard Manager",
		                subtitle: "Sunuculara göre filtreleyin, favorilerinizi seçin ve doğrudan Macro Deck butonlarına aktarın.",
		                connecting: "Bağlanıyor...",
		                waitingConnection: "Bağlantı Bekleniyor",
		                connectionError: "Sunucu Bağlantı Hatası",
		                soundsLoaded: (user, count) => `${user || 'Discord'} (${count} ses)`,
		                restartBtn: "🔄 Yeniden Başlat",
		                restartTitle: "Macro Deck'i Yeniden Başlat",
		                closeBtn: "✕ Kapat",
		                closeTitle: "Pencereyi Kapat",
		                allFilter: "🌟 Tümü",
		                favsFilter: "⭐ Favorilerim",
		                defaultFilter: "🔊 Discord Varsayılan",
		                serverPlaceholder: "🏰 Belirli Bir Sunucu Seç...",
		                searchPlaceholder: "🔍 Ses veya sunucu adına göre ara...",
		                targetFolderLabel: "📁 Hedef Klasör:",
		                loading: "Yükleniyor...",
		                defaultFolder: "(Varsayılan Klasör)",
		                currentFolderSuffix: " (Bu Klasör)",
		                selectAllBtn: "Görünenleri Seç / Temizle",
		                syncBtn: (count) => `🚀 Seçilenleri Macro Deck'e Aktar (<span id="selCount">${count}</span>)`,
		                syncingBtn: "Aktarılıyor...",
		                emptySounds: "Eşleşen ses bulunamadı.",
		                loadingSounds: "Sesler yükleniyor...",
		                soundsFetchError: "Sesler alınamadı. Discord uygulamasının açık olduğundan emin olun.",
		                playBtn: "▶ Çal",
		                playTooltip: "Önizleme Çal",
		                addFavTooltip: "Favorilere Ekle",
		                removeFavTooltip: "Favorilerden Çıkar",
		                toastPlayed: (name) => `"${name}" başarıyla çalındı! 🔊`,
		                toastPlayError: "Ses çalınamadı (Discord'da ses kanalında mısınız?)",
		                toastPlayReqError: "Çalma isteği iletilemedi.",
		                toastNoSelection: "Lütfen önce aktarmak istediğiniz sesleri seçin!",
		                toastSyncError: "Aktarım sırasında hata oluştu.",
		                toastConnError: "Sunucuya bağlanılamadı.",
		                toastRestarting: "Macro Deck 1-2 saniye içinde yeniden başlatılıyor...",
		                restartConfirm: "Macro Deck şimdi yeniden başlatılsın mı?",
		                modalTitle: "🎉 Sesler Aktarıldı!",
		                modalMsg: (count, folder) => `<strong>${count} ses</strong> başarıyla Macro Deck "<strong>${folder}</strong>" klasörüne buton olarak eklendi ve tüm buton simgeleri hazırlandı.<br><br>Macro Deck'in butonları ve simgeleri hemen ekranda göstermesi için yeniden başlatabilirsiniz:`,
		                modalRestartBtn: "🔄 Macro Deck'i Yeniden Başlat (Hemen Görünsün)",
		                modalLaterBtn: "Tamam (Daha Sonra Başlat)",
		                serverSoundCount: (name, count) => `${name} (${count} ses)`,
		                defaultGuildName: "Discord (Varsayılan)",
		                serverFallback: (id) => `Sunucu (${id})`
		            }
		        };

		        let currentLang = localStorage.getItem('soundboard_lang') || 'en';

		        function t(key, ...args) {
		            const langObj = i18n[currentLang] || i18n['en'];
		            const val = langObj[key] !== undefined ? langObj[key] : (i18n['en'][key] || key);
		            if (typeof val === 'function') return val(...args);
		            return val;
		        }

		        function changeLanguage(lang) {
		            currentLang = lang;
		            localStorage.setItem('soundboard_lang', lang);
		            applyLanguage();
		        }

		        function applyLanguage() {
		            document.getElementById('langSelect').value = currentLang;
		            document.getElementById('txtHeaderSubtitle').textContent = t('subtitle');
		            document.getElementById('btnRestart').textContent = t('restartBtn');
		            document.getElementById('btnRestart').title = t('restartTitle');
		            document.getElementById('btnClose').textContent = t('closeBtn');
		            document.getElementById('btnClose').title = t('closeTitle');
		            document.getElementById('txtFilterAll').textContent = t('allFilter');
		            document.getElementById('txtFilterFavs').textContent = t('favsFilter');
		            document.getElementById('txtFilterDefault').textContent = t('defaultFilter');
		            document.getElementById('searchInput').placeholder = t('searchPlaceholder');
		            document.getElementById('lblTargetFolder').textContent = t('targetFolderLabel');
		            document.getElementById('btnSelectAll').textContent = t('selectAllBtn');
		            btnSync.innerHTML = t('syncBtn', selected.size);
		            document.getElementById('modalTitle').textContent = t('modalTitle');
		            document.getElementById('modalBtnRestart').textContent = t('modalRestartBtn');
		            document.getElementById('modalBtnLater').textContent = t('modalLaterBtn');

		            if (cachedFolders) populateFolderDropdown(cachedFolders);
		            populateServerDropdown();
		            renderGrid();
		        }

		        let allSounds = [];
		        let favorites = new Set();
		        let activeFilter = 'all';
		        let cachedFolders = null;
		        let lastStatus = null;
		        const selected = new Set();

		        const grid = document.getElementById('soundGrid');
		        const searchInput = document.getElementById('searchInput');
		        const targetFolderSelect = document.getElementById('targetFolderSelect');
		        const btnSync = document.getElementById('btnSync');
		        const btnSelectAll = document.getElementById('btnSelectAll');
		        const selCount = document.getElementById('selCount');
		        const toast = document.getElementById('toast');
		        const statusText = document.getElementById('statusText');
		        const successModal = document.getElementById('successModal');
		        const modalMessage = document.getElementById('modalMessage');
		        const serverSelect = document.getElementById('serverSelect');
		        const filterChips = document.getElementById('filterChips');
		        const countAll = document.getElementById('countAll');
		        const countFavs = document.getElementById('countFavs');
		        const countDefault = document.getElementById('countDefault');

		        function showToast(msg, bg = '#23a55a') {
		            toast.textContent = msg;
		            toast.style.background = bg;
		            toast.classList.add('show');
		            setTimeout(() => toast.classList.remove('show'), 3500);
		        }

		        function closeModal() {
		            successModal.classList.remove('active');
		        }

		        async function restartMacroDeckNow() {
		            showToast(t('toastRestarting'), '#5865F2');
		            try {
		                await fetch('/api/restart-macrodeck', { method: 'POST' });
		                setTimeout(() => window.close(), 1800);
		            } catch (e) {
		                showToast(t('toastPlayReqError'));
		            }
		        }

		        async function restartMacroDeck() {
		            if (confirm(t('restartConfirm'))) {
		                await restartMacroDeckNow();
		            }
		        }

		        async function loadSoundsAndFavorites() {
		            try {
		                const stRes = await fetch('/api/status');
		                lastStatus = await stRes.json();
		                updateStatusBadge();

		                const [soundsRes, favsRes, foldersRes] = await Promise.all([
		                    fetch('/api/sounds'),
		                    fetch('/api/favorites'),
		                    fetch('/api/folders')
		                ]);

		                allSounds = await soundsRes.json();
		                const favList = await favsRes.json();
		                favorites = new Set(favList);

		                cachedFolders = await foldersRes.json();
		                populateFolderDropdown(cachedFolders);

		                populateServerDropdown();
		                updateCounts();
		                renderGrid();
		            } catch (e) {
		                statusText.textContent = t('connectionError');
		                grid.innerHTML = `<div style="color:var(--danger); grid-column:1/-1; text-align:center;">${t('soundsFetchError')}</div>`;
		            }
		        }

		        function updateStatusBadge() {
		            if (!lastStatus) return;
		            if (lastStatus.connected && lastStatus.authenticated) {
		                statusText.textContent = t('soundsLoaded', lastStatus.username, lastStatus.soundCount);
		            } else {
		                statusText.textContent = t('waitingConnection');
		            }
		        }

		        function populateFolderDropdown(folders) {
		            targetFolderSelect.innerHTML = '';
		            if (!folders || folders.length === 0) {
		                targetFolderSelect.innerHTML = `<option value="">${t('defaultFolder')}</option>`;
		                return;
		            }
		            let hasCaller = false;
		            folders.forEach(f => {
		                const opt = document.createElement('option');
		                opt.value = f.id;
		                if (f.isCaller) {
		                    opt.textContent = `⭐ ${f.name}${t('currentFolderSuffix')}`;
		                    opt.selected = true;
		                    hasCaller = true;
		                } else {
		                    opt.textContent = `📁 ${f.name}`;
		                }
		                targetFolderSelect.appendChild(opt);
		            });
		            if (!hasCaller) {
		                const fallback = folders.find(f => (f.name || '').toLowerCase() === 'ses paneli') ||
		                                 folders.find(f => (f.name || '').toLowerCase() === 'discord') ||
		                                 folders[0];
		                if (fallback) targetFolderSelect.value = fallback.id;
		            }
		        }

		        function updateCounts() {
		            countAll.textContent = allSounds.length;
		            countFavs.textContent = favorites.size;
		            countDefault.textContent = allSounds.filter(s => s.guild_id === '0' || !s.guild_id).length;
		        }

		        function populateServerDropdown() {
		            const guildMap = new Map();
		            allSounds.forEach(s => {
		                if (s.guild_id && s.guild_id !== '0') {
		                    const name = s.guild_name || t('serverFallback', s.guild_id);
		                    guildMap.set(s.guild_id, (guildMap.get(s.guild_id) || { name, count: 0 }));
		                    guildMap.get(s.guild_id).count++;
		                }
		            });

		            const currentVal = serverSelect.value;
		            serverSelect.innerHTML = `<option value="">${t('serverPlaceholder')}</option>`;
		            Array.from(guildMap.entries())
		                .sort((a, b) => a[1].name.localeCompare(b[1].name, currentLang))
		                .forEach(([gid, info]) => {
		                    const opt = document.createElement('option');
		                    opt.value = gid;
		                    opt.textContent = t('serverSoundCount', info.name, info.count);
		                    if (gid === currentVal) opt.selected = true;
		                    serverSelect.appendChild(opt);
		                });
		        }

		        filterChips.addEventListener('click', (e) => {
		            const chip = e.target.closest('.filter-chip');
		            if (!chip) return;
		            document.querySelectorAll('.filter-chip').forEach(c => c.classList.remove('active'));
		            chip.classList.add('active');
		            activeFilter = chip.dataset.filter;
		            serverSelect.value = '';
		            renderGrid();
		        });

		        serverSelect.addEventListener('change', () => {
		            if (serverSelect.value) {
		                document.querySelectorAll('.filter-chip').forEach(c => c.classList.remove('active'));
		                activeFilter = serverSelect.value;
		            } else {
		                activeFilter = 'all';
		                document.querySelector('.filter-chip[data-filter="all"]').classList.add('active');
		            }
		            renderGrid();
		        });

		        function getFilteredSounds() {
		            const query = searchInput.value.toLowerCase().trim();
		            return allSounds.filter(s => {
		                if (activeFilter === 'favorites') {
		                    if (!favorites.has(s.sound_id)) return false;
		                } else if (activeFilter === '0') {
		                    if (s.guild_id !== '0' && s.guild_id) return false;
		                } else if (activeFilter !== 'all' && activeFilter !== '') {
		                    if (s.guild_id !== activeFilter) return false;
		                }

		                if (query) {
		                    const nameMatch = s.name.toLowerCase().includes(query);
		                    const guildMatch = (s.guild_name || '').toLowerCase().includes(query);
		                    if (!nameMatch && !guildMatch) return false;
		                }

		                return true;
		            });
		        }

		        function getResolvedGuildName(s) {
		            if (s.guild_id === '0' || !s.guild_id || s.guild_name === 'Discord (Default)' || s.guild_name === 'Discord (Varsayılan)') {
		                return t('defaultGuildName');
		            }
		            return s.guild_name || t('serverFallback', s.guild_id);
		        }

		        function renderGrid() {
		            const filtered = getFilteredSounds();
		            grid.innerHTML = '';
		            if (filtered.length === 0) {
		                grid.innerHTML = `<div style="color: var(--muted); grid-column: 1 / -1; text-align: center; padding: 40px;">${t('emptySounds')}</div>`;
		                return;
		            }

		            filtered.forEach(s => {
		                const card = document.createElement('div');
		                card.className = `sound-card ${selected.has(s.sound_id) ? 'selected' : ''}`;

		                let iconHtml = '🔊';
		                if (s.emoji_id) {
		                    iconHtml = `<img src="https://cdn.discordapp.com/emojis/${s.emoji_id}.png?size=96" alt="" loading="lazy">`;
		                } else if (s.emoji_name) {
		                    iconHtml = s.emoji_name;
		                }

		                const isFav = favorites.has(s.sound_id);
		                const displayGuild = getResolvedGuildName(s);

		                card.innerHTML = `
		                    <div class="card-top">
		                        <div class="sound-icon">${iconHtml}</div>
		                        <div class="sound-info">
		                            <div class="sound-name" title="${s.name}">${s.name}</div>
		                            <div class="sound-guild" title="${displayGuild}">${displayGuild}</div>
		                        </div>
		                        <button class="btn-fav ${isFav ? 'favorited' : ''}" title="${isFav ? t('removeFavTooltip') : t('addFavTooltip')}">${isFav ? '★' : '☆'}</button>
		                    </div>
		                    <div class="card-bottom">
		                        <button class="btn-play" title="${t('playTooltip')}">${t('playBtn')}</button>
		                        <div class="checkbox-custom"></div>
		                    </div>
		                `;

		                // Toggle Select on card click
		                card.addEventListener('click', (e) => {
		                    if (e.target.closest('.btn-play') || e.target.closest('.btn-fav')) return;
		                    toggleSelect(s.sound_id);
		                });

		                // Star toggle
		                const favBtn = card.querySelector('.btn-fav');
		                favBtn.addEventListener('click', async (e) => {
		                    e.stopPropagation();
		                    const newFavState = !favorites.has(s.sound_id);
		                    if (newFavState) favorites.add(s.sound_id);
		                    else favorites.delete(s.sound_id);

		                    favBtn.classList.toggle('favorited', newFavState);
		                    favBtn.textContent = newFavState ? '★' : '☆';
		                    updateCounts();

		                    try {
		                        await fetch('/api/favorites', {
		                            method: 'POST',
		                            headers: { 'Content-Type': 'application/json' },
		                            body: JSON.stringify({ soundId: s.sound_id, isFavorite: newFavState })
		                        });
		                    } catch (e) { }

		                    if (activeFilter === 'favorites' && !newFavState) {
		                        card.remove();
		                    }
		                });

		                // Play button
		                const playBtn = card.querySelector('.btn-play');
		                playBtn.addEventListener('click', async (e) => {
		                    e.stopPropagation();
		                    try {
		                        const res = await fetch(`/api/play?id=${encodeURIComponent(s.sound_id)}&guild_id=${encodeURIComponent(s.guild_id || '')}`);
		                        const json = await res.json();
		                        if (json.success) {
		                            showToast(t('toastPlayed', s.name));
		                        } else {
		                            showToast(t('toastPlayError'), '#da373c');
		                        }
		                    } catch (err) {
		                        showToast(t('toastPlayReqError'), '#da373c');
		                    }
		                });

		                grid.appendChild(card);
		            });
		        }

		        function toggleSelect(id) {
		            if (selected.has(id)) selected.delete(id);
		            else selected.add(id);
		            selCount.textContent = selected.size;
		            btnSync.disabled = selected.size === 0;
		            renderGrid();
		        }

		        btnSelectAll.addEventListener('click', () => {
		            const visible = getFilteredSounds();
		            const allVisibleSelected = visible.every(s => selected.has(s.sound_id));
		            if (allVisibleSelected) {
		                visible.forEach(s => selected.delete(s.sound_id));
		            } else {
		                visible.forEach(s => selected.add(s.sound_id));
		            }
		            selCount.textContent = selected.size;
		            btnSync.disabled = selected.size === 0;
		            renderGrid();
		        });

		        searchInput.addEventListener('input', renderGrid);

		        btnSync.addEventListener('click', async () => {
		            if (selected.size === 0) {
		                showToast(t('toastNoSelection'));
		                return;
		            }
		            btnSync.disabled = true;
		            btnSync.textContent = t('syncingBtn');
		            try {
		                const folderId = targetFolderSelect.value;
		                const res = await fetch('/api/sync', {
		                    method: 'POST',
		                    headers: { 'Content-Type': 'application/json' },
		                    body: JSON.stringify({
		                        soundIds: Array.from(selected),
		                        folderId: folderId
		                    })
		                });
		                const data = await res.json();
		                if (data.success) {
		                    modalMessage.innerHTML = t('modalMsg', data.count, data.folderName);
		                    successModal.classList.add('active');
		                } else {
		                    showToast(t('toastSyncError'), '#da373c');
		                }
		            } catch (e) {
		                showToast(t('toastConnError'), '#da373c');
		            } finally {
		                btnSync.disabled = false;
		                btnSync.innerHTML = t('syncBtn', selected.size);
		            }
		        });

		        window.addEventListener('beforeunload', () => {
		            try {
		                navigator.sendBeacon('/api/close');
		            } catch (e) { }
		        });

		        applyLanguage();
		        loadSoundsAndFavorites();
		    </script>
		</body>
		</html>
		""";

		var bytes = Encoding.UTF8.GetBytes(html);
		resp.ContentType = "text/html; charset=utf-8";
		resp.ContentLength64 = bytes.Length;
		await resp.OutputStream.WriteAsync(bytes);
		resp.Close();
	}

	public async ValueTask DisposeAsync()
	{
		Stop();
		await Task.CompletedTask;
	}
}

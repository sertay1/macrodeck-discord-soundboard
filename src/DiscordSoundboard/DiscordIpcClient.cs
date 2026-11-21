using System.IO.Pipes;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Serilog;

namespace DiscordSoundboard;

public sealed class DiscordSound
{
	[JsonPropertyName("sound_id")]
	public string SoundId { get; set; } = string.Empty;

	[JsonPropertyName("name")]
	public string Name { get; set; } = string.Empty;

	[JsonPropertyName("guild_id")]
	public string? GuildId { get; set; }

	[JsonPropertyName("guild_name")]
	public string? GuildName { get; set; }

	[JsonPropertyName("volume")]
	public double Volume { get; set; } = 1.0;

	[JsonPropertyName("emoji_id")]
	public string? EmojiId { get; set; }

	[JsonPropertyName("emoji_name")]
	public string? EmojiName { get; set; }

	public string DisplayName => !string.IsNullOrEmpty(EmojiName) && string.IsNullOrEmpty(EmojiId)
		? $"{EmojiName} {Name}"
		: Name;
}

public sealed class DiscordIpcClient : IAsyncDisposable
{
	private const int OpcodeHandshake = 0;
	private const int OpcodeFrame = 1;
	private const int OpcodeClose = 2;
	private const int OpcodePing = 3;
	private const int OpcodePong = 4;

	private readonly ILogger _logger;
	private readonly HttpClient _httpClient;
	private readonly SemaphoreSlim _sendLock = new(1, 1);
	private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pendingNonces = new();

	public HttpClient HttpClient => _httpClient;

	private NamedPipeClientStream? _pipe;
	private CancellationTokenSource? _readCts;
	private Task? _readTask;

	public string ClientId { get; set; } = string.Empty;
	public string ClientSecret { get; set; } = string.Empty;
	public string AccessToken { get; set; } = string.Empty;
	public string RefreshToken { get; set; } = string.Empty;

	public bool IsConnected => _pipe?.IsConnected == true;
	public bool IsAuthenticated { get; private set; }
	public string? CurrentUsername { get; private set; }
	public List<DiscordSound> Sounds { get; private set; } = [];
	public IReadOnlyDictionary<string, string> Guilds => _guilds;
	private Dictionary<string, string> _guilds = new();

	public event Action? StateChanged;
	public event Action<IReadOnlyList<DiscordSound>>? SoundsUpdated;

	public DiscordIpcClient(ILogger logger, HttpClient? httpClient = null)
	{
		_logger = logger.ForContext<DiscordIpcClient>();
		_httpClient = httpClient ?? new HttpClient();
		_guilds = LoadCachedGuilds();
		LoadCachedSounds();
	}

	private static string GetCacheFilePath()
	{
		var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		var dir = Path.Combine(appData, "MacroDeck", "plugins", "com.sertaycil.discord-soundboard");
		Directory.CreateDirectory(dir);
		return Path.Combine(dir, "sounds_cache.json");
	}

	public void LoadCachedSounds()
	{
		try
		{
			var cachePath = GetCacheFilePath();
			if (File.Exists(cachePath))
			{
				var json = File.ReadAllText(cachePath);
				var list = JsonSerializer.Deserialize<List<DiscordSound>>(json);
				if (list != null && list.Count > 0)
				{
					foreach (var s in list)
					{
						ApplyGuildName(s);
					}
					Sounds = list;
					_logger.Information("Loaded {Count} cached sounds from {Path}", list.Count, cachePath);
					SoundsUpdated?.Invoke(list);
				}
			}
		}
		catch (Exception ex)
		{
			_logger.Warning(ex, "Could not load cached sounds.");
		}
	}

	private void ApplyGuildName(DiscordSound s)
	{
		if (s.GuildId == "0" || string.IsNullOrEmpty(s.GuildId))
		{
			s.GuildName = "Discord (Default)";
		}
		else if (_guilds.TryGetValue(s.GuildId, out var gname))
		{
			s.GuildName = gname;
		}
		else if (string.IsNullOrEmpty(s.GuildName))
		{
			s.GuildName = $"Server ({s.GuildId})";
		}
	}

	private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

	private void SaveCachedSounds(List<DiscordSound> list)
	{
		try
		{
			var cachePath = GetCacheFilePath();
			var json = JsonSerializer.Serialize(list, IndentedJsonOptions);
			File.WriteAllText(cachePath, json);
		}
		catch (Exception ex)
		{
			_logger.Warning(ex, "Could not save cached sounds.");
		}
	}

	private static string GetGuildsCacheFilePath()
	{
		var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		var dir = Path.Combine(appData, "MacroDeck", "plugins", "com.sertaycil.discord-soundboard");
		Directory.CreateDirectory(dir);
		return Path.Combine(dir, "guilds_cache.json");
	}

	private static void SaveCachedGuilds(Dictionary<string, string> guilds)
	{
		try
		{
			var cachePath = GetGuildsCacheFilePath();
			var json = JsonSerializer.Serialize(guilds, IndentedJsonOptions);
			File.WriteAllText(cachePath, json);
		}
		catch { }
	}

	private static Dictionary<string, string> LoadCachedGuilds()
	{
		try
		{
			var cachePath = GetGuildsCacheFilePath();
			if (File.Exists(cachePath))
			{
				var json = File.ReadAllText(cachePath);
				return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
			}
		}
		catch { }
		return new();
	}

	public async Task<bool> ConnectAsync(string clientId, CancellationToken cancellationToken = default)
	{
		ClientId = clientId;
		await DisconnectAsync();

		for (var i = 0; i < 10; i++)
		{
			try
			{
				var pipeName = $"discord-ipc-{i}";
				_logger.Information("Connecting to \\\\.\\pipe\\{PipeName}...", pipeName);

				var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
				await pipe.ConnectAsync(1000, cancellationToken);

				_pipe = pipe;
				_readCts = new CancellationTokenSource();
				_readTask = Task.Run(() => ReadLoopAsync(_readCts.Token), CancellationToken.None);

				// Send Handshake
				var handshakePayload = new JsonObject
				{
					["v"] = 1,
					["client_id"] = clientId
				};

				await SendPacketAsync(OpcodeHandshake, handshakePayload, cancellationToken);
				_logger.Information("Connected to Discord on {PipeName}", pipeName);
				StateChanged?.Invoke();
				return true;
			}
			catch (TimeoutException)
			{
				// Pipe not active, try next
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				_logger.Debug(ex, "Failed connecting to discord-ipc-{Index}", i);
			}
		}

		_logger.Warning("Discord client not found on any IPC pipe.");
		return false;
	}

	public async Task<bool> AuthenticateAsync(string accessToken, CancellationToken cancellationToken = default)
	{
		AccessToken = accessToken;
		var nonce = Guid.NewGuid().ToString("N");

		var payload = new JsonObject
		{
			["cmd"] = "AUTHENTICATE",
			["args"] = new JsonObject
			{
				["access_token"] = accessToken
			},
			["nonce"] = nonce
		};

		try
		{
			var response = await SendCommandAsync(payload, nonce, cancellationToken);
			if (response.TryGetProperty("evt", out var evt) && evt.GetString() == "ERROR")
			{
				_logger.Warning("Discord AUTHENTICATE failed: {Response}", response.ToString());
				IsAuthenticated = false;
				StateChanged?.Invoke();
				return false;
			}

			IsAuthenticated = true;
			_logger.Information("Successfully authenticated with Discord!");
			StateChanged?.Invoke();

			// Fetch guilds & sounds immediately
			_ = Task.Run(async () =>
			{
				await FetchGuildsAsync(CancellationToken.None);
				await FetchSoundsAsync(CancellationToken.None);
			}, CancellationToken.None);
			return true;
		}
		catch (Exception ex)
		{
			_logger.Error(ex, "Error during Discord AUTHENTICATE");
			IsAuthenticated = false;
			StateChanged?.Invoke();
			return false;
		}
	}

	public async Task<bool> AuthorizeAndAuthenticateAsync(string clientId, string clientSecret, CancellationToken cancellationToken = default)
	{
		ClientSecret = clientSecret;

		// Ensure socket handshake always matches the Client ID being authorized
		if (!IsConnected || !string.Equals(ClientId, clientId, StringComparison.Ordinal))
		{
			var connected = await ConnectAsync(clientId, cancellationToken);
			if (!connected) return false;
		}

		var nonce = Guid.NewGuid().ToString("N");
		var payload = new JsonObject
		{
			["cmd"] = "AUTHORIZE",
			["args"] = new JsonObject
			{
				["client_id"] = clientId,
				["scopes"] = new JsonArray { "rpc", "rpc.voice.write" }
			},
			["nonce"] = nonce
		};

		_logger.Information("Prompting user to AUTHORIZE on Discord desktop...");
		var response = await SendCommandAsync(payload, nonce, cancellationToken);

		if (response.TryGetProperty("evt", out var evt) && evt.GetString() == "ERROR")
		{
			_logger.Warning("User declined authorization or error occurred: {Response}", response.ToString());
			return false;
		}

		if (!response.TryGetProperty("data", out var data) || !data.TryGetProperty("code", out var codeProp))
		{
			_logger.Warning("Authorize response did not contain code.");
			return false;
		}

		var code = codeProp.GetString();
		if (string.IsNullOrEmpty(code)) return false;

		_logger.Information("Exchanging OAuth code for token...");
		var token = await ExchangeCodeForTokenAsync(clientId, clientSecret, code, cancellationToken);
		if (string.IsNullOrEmpty(token))
		{
			_logger.Warning("Failed to exchange OAuth code for access token.");
			return false;
		}

		return await AuthenticateAsync(token, cancellationToken);
	}

	public async Task<IReadOnlyDictionary<string, string>> FetchGuildsAsync(CancellationToken cancellationToken = default)
	{
		var nonce = Guid.NewGuid().ToString("N");
		var payload = new JsonObject
		{
			["cmd"] = "GET_GUILDS",
			["args"] = new JsonObject(),
			["nonce"] = nonce
		};

		try
		{
			var response = await SendCommandAsync(payload, nonce, cancellationToken);
			if (response.TryGetProperty("data", out var data) &&
			    data.TryGetProperty("guilds", out var guildsArr) &&
			    guildsArr.ValueKind == JsonValueKind.Array)
			{
				var dict = new Dictionary<string, string>();
				foreach (var g in guildsArr.EnumerateArray())
				{
					var id = g.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
					var name = g.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
					if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
					{
						dict[id] = name;
					}
				}
				_guilds = dict;
				SaveCachedGuilds(dict);
				_logger.Information("Retrieved {Count} guilds from Discord.", dict.Count);

				foreach (var s in Sounds)
				{
					ApplyGuildName(s);
				}

				return dict;
			}
		}
		catch (Exception ex)
		{
			_logger.Warning(ex, "Failed to fetch guilds from Discord RPC");
		}

		return _guilds;
	}

	public async Task<IReadOnlyList<DiscordSound>> FetchSoundsAsync(CancellationToken cancellationToken = default)
	{
		var nonce = Guid.NewGuid().ToString("N");
		var payload = new JsonObject
		{
			["cmd"] = "GET_SOUNDBOARD_SOUNDS",
			["args"] = new JsonObject(),
			["nonce"] = nonce
		};

		try
		{
			var response = await SendCommandAsync(payload, nonce, cancellationToken);
			if (response.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
			{
				var list = JsonSerializer.Deserialize<List<DiscordSound>>(data.GetRawText()) ?? [];
				foreach (var s in list)
				{
					ApplyGuildName(s);
				}
				Sounds = list;
				_logger.Information("Retrieved {Count} sounds from Discord.", list.Count);
				SoundsUpdated?.Invoke(list);
				SaveCachedSounds(list);
				return list;
			}
		}
		catch (Exception ex)
		{
			_logger.Error(ex, "Failed to fetch soundboard sounds");
		}

		return Sounds;
	}

	public async Task<bool> PlaySoundAsync(string soundId, string? guildId = null, CancellationToken cancellationToken = default)
	{
		if (!IsConnected || !IsAuthenticated)
		{
			_logger.Warning("Cannot play sound: Discord is not connected or authenticated.");
			return false;
		}

		if (string.IsNullOrEmpty(guildId))
		{
			var matching = Sounds.FirstOrDefault(s => s.SoundId == soundId);
			guildId = matching?.GuildId ?? "0";
		}

		var nonce = Guid.NewGuid().ToString("N");
		var payload = new JsonObject
		{
			["cmd"] = "PLAY_SOUNDBOARD_SOUND",
			["args"] = new JsonObject
			{
				["sound_id"] = soundId,
				["guild_id"] = guildId
			},
			["nonce"] = nonce
		};

		_logger.Information("Playing sound {SoundId} (guild_id: {GuildId})...", soundId, guildId);
		try
		{
			var response = await SendCommandAsync(payload, nonce, cancellationToken);
			if (response.TryGetProperty("evt", out var evt) && evt.GetString() == "ERROR")
			{
				_logger.Warning("Discord play sound error: {Response}", response.ToString());
				return false;
			}

			_logger.Information("Sound {SoundId} played successfully!", soundId);
			return true;
		}
		catch (Exception ex)
		{
			_logger.Error(ex, "Error sending PLAY_SOUNDBOARD_SOUND");
			return false;
		}
	}

	private async Task<string?> ExchangeCodeForTokenAsync(string clientId, string clientSecret, string code, CancellationToken cancellationToken)
	{
		const string tokenUrl = "https://discord.com/api/v10/oauth2/token";
		var redirectUris = new[] { "https://localhost/", "", "http://127.0.0.1", "http://localhost" };

		foreach (var redirectUri in redirectUris)
		{
			try
			{
				var dict = new Dictionary<string, string>
				{
					["client_id"] = clientId,
					["client_secret"] = clientSecret,
					["grant_type"] = "authorization_code",
					["code"] = code
				};
				if (!string.IsNullOrEmpty(redirectUri))
				{
					dict["redirect_uri"] = redirectUri;
				}

				using var req = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
				{
					Content = new FormUrlEncodedContent(dict)
				};

				var resp = await _httpClient.SendAsync(req, cancellationToken);
				var content = await resp.Content.ReadAsStringAsync(cancellationToken);

				using var doc = JsonDocument.Parse(content);
				if (doc.RootElement.TryGetProperty("access_token", out var at))
				{
					AccessToken = at.GetString() ?? string.Empty;
					if (doc.RootElement.TryGetProperty("refresh_token", out var rt))
					{
						RefreshToken = rt.GetString() ?? string.Empty;
					}
					return AccessToken;
				}
			}
			catch (Exception ex)
			{
				_logger.Debug(ex, "Failed token exchange attempt with redirect '{Redirect}'", redirectUri);
			}
		}

		return null;
	}

	private async Task<JsonElement> SendCommandAsync(JsonObject payload, string nonce, CancellationToken cancellationToken)
	{
		var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_pendingNonces)
		{
			_pendingNonces[nonce] = tcs;
		}

		using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

		try
		{
			await SendPacketAsync(OpcodeFrame, payload, cancellationToken);
			return await tcs.Task;
		}
		finally
		{
			lock (_pendingNonces)
			{
				_pendingNonces.Remove(nonce);
			}
		}
	}

	private async Task SendPacketAsync(int op, JsonNode payload, CancellationToken cancellationToken)
	{
		if (_pipe == null || !_pipe.IsConnected)
		{
			throw new InvalidOperationException("Discord named pipe is not connected.");
		}

		var json = payload.ToJsonString();
		var bytes = Encoding.UTF8.GetBytes(json);
		var header = new byte[8];

		BitConverter.TryWriteBytes(header.AsSpan(0, 4), op);
		BitConverter.TryWriteBytes(header.AsSpan(4, 4), bytes.Length);

		await _sendLock.WaitAsync(cancellationToken);
		try
		{
			await _pipe.WriteAsync(header, cancellationToken);
			await _pipe.WriteAsync(bytes, cancellationToken);
			await _pipe.FlushAsync(cancellationToken);
		}
		finally
		{
			_sendLock.Release();
		}
	}

	private async Task ReadLoopAsync(CancellationToken cancellationToken)
	{
		var header = new byte[8];

		try
		{
			while (!cancellationToken.IsCancellationRequested && _pipe?.IsConnected == true)
			{
				await ReadExactAsync(_pipe, header, 8, cancellationToken);

				var op = BitConverter.ToInt32(header, 0);
				var length = BitConverter.ToInt32(header, 4);

				if (length < 0 || length > 10 * 1024 * 1024)
				{
					_logger.Warning("Invalid packet length: {Length}", length);
					break;
				}

				var payloadBytes = new byte[length];
				await ReadExactAsync(_pipe, payloadBytes, length, cancellationToken);

				var jsonStr = Encoding.UTF8.GetString(payloadBytes);
				HandleIncomingMessage(op, jsonStr);
			}
		}
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			_logger.Warning("Discord IPC read loop terminated: {Message}", ex.Message);
		}
		finally
		{
			IsAuthenticated = false;
			StateChanged?.Invoke();
		}
	}

	private static async Task ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
	{
		var totalRead = 0;
		while (totalRead < count)
		{
			var read = await stream.ReadAsync(buffer.AsMemory(totalRead, count - totalRead), cancellationToken);
			if (read == 0)
			{
				throw new EndOfStreamException("Discord pipe stream closed.");
			}
			totalRead += read;
		}
	}

	private void HandleIncomingMessage(int op, string jsonStr)
	{
		try
		{
			using var doc = JsonDocument.Parse(jsonStr);
			var root = doc.RootElement.Clone();

			if (root.TryGetProperty("evt", out var evt) && evt.GetString() == "READY")
			{
				if (root.TryGetProperty("data", out var data) && data.TryGetProperty("user", out var user))
				{
					if (user.TryGetProperty("username", out var u)) CurrentUsername = u.GetString();
				}
				_logger.Information("Discord IPC Ready. Logged in as: {User}", CurrentUsername);
				StateChanged?.Invoke();

				// If we have access token, auto authenticate
				if (!string.IsNullOrEmpty(AccessToken))
				{
					_ = AuthenticateAsync(AccessToken);
				}
				return;
			}

			if (root.TryGetProperty("nonce", out var nonceProp))
			{
				var nonce = nonceProp.GetString();
				if (!string.IsNullOrEmpty(nonce))
				{
					TaskCompletionSource<JsonElement>? tcs;
					lock (_pendingNonces)
					{
						_pendingNonces.TryGetValue(nonce, out tcs);
					}
					tcs?.TrySetResult(root);
				}
			}
		}
		catch (Exception ex)
		{
			_logger.Error(ex, "Error processing incoming Discord message");
		}
	}

	public async ValueTask DisconnectAsync()
	{
		_readCts?.Cancel();
		if (_pipe != null)
		{
			try
			{
				await _pipe.DisposeAsync();
			}
			catch { }
			_pipe = null;
		}
		IsAuthenticated = false;
		StateChanged?.Invoke();
	}

	public async ValueTask DisposeAsync()
	{
		await DisconnectAsync();
		_sendLock.Dispose();
		_httpClient.Dispose();
	}
}

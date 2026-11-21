using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Serilog;

namespace DiscordSoundboard;

public sealed class PlayRandomSoundAction : IActionDefinition, IDynamicOptionsActionDefinition
{
	private const string PoolParameter = "pool";
	private const string VolumeParameter = "volume";

	private readonly DiscordIpcClient _discordClient;
	private readonly ILogger _logger;

	public PlayRandomSoundAction(DiscordIpcClient discordClient, ILogger logger)
	{
		_discordClient = discordClient;
		_logger = logger.ForContext<PlayRandomSoundAction>();

		Parameters =
		[
			ActionParameter.DynamicChoice(
				PoolParameter,
				label: LocalizedText.FromLiteral("Sound Pool"),
				description: LocalizedText.FromLiteral("Choose whether to pick from all sounds, favorites only, or a specific server."),
				optionsSourceId: null!,
				placeholder: LocalizedText.FromLiteral("Select sound pool..."),
				required: false),
			ActionParameter.Slider(
				VolumeParameter,
				0,
				100,
				label: LocalizedText.FromLiteral("Volume (%)"),
				description: LocalizedText.FromLiteral("Playback volume (0-100%). Default is 100%."),
				step: 5,
				defaultValue: 100)
		];
	}

	public string Id => "play-random-sound";

	public LocalizedText Name => LocalizedText.FromLiteral("Play Random Sound");

	public LocalizedText Description => LocalizedText.FromLiteral("Plays a random soundboard sound from your Discord servers in voice channel.");

	public IReadOnlyList<ActionParameter> Parameters { get; }

	public MacroDeckPlatform Platforms => MacroDeckPlatform.All;

	public async Task<DynamicOptionsResult> GetDynamicOptionsAsync(DynamicOptionsContext context, CancellationToken cancellationToken)
	{
		var sounds = _discordClient.Sounds;
		if (sounds.Count == 0)
		{
			_discordClient.LoadCachedSounds();
			sounds = _discordClient.Sounds;
		}

		if (sounds.Count == 0 && _discordClient.IsConnected && _discordClient.IsAuthenticated)
		{
			sounds = (await _discordClient.FetchSoundsAsync(cancellationToken)).ToList();
		}

		var options = new List<ActionParameterOption>
		{
			new() { Value = "all", Label = LocalizedText.FromLiteral("🌟 All Sounds (Tüm Sesler)") },
			new() { Value = "favorites", Label = LocalizedText.FromLiteral("⭐ Favorites Only (Sadece Favorilerim)") }
		};

		var servers = sounds
			.Where(s => !string.IsNullOrEmpty(s.GuildId) && s.GuildId != "0")
			.GroupBy(s => s.GuildId!)
			.Select(g => new { GuildId = g.Key, Name = g.First().GuildName ?? $"Server ({g.Key})", Count = g.Count() })
			.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase);

		foreach (var s in servers)
		{
			options.Add(new ActionParameterOption
			{
				Value = $"guild:{s.GuildId}",
				Label = LocalizedText.FromLiteral($"🏰 [{s.Name}] ({s.Count} sounds)")
			});
		}

		return new DynamicOptionsResult
		{
			Options = options,
			AllowsCustomValue = false
		};
	}

	public IActionExecutor CreateExecutor() => new Executor(_discordClient, _logger);

	private sealed class Executor : IActionExecutor
	{
		private readonly DiscordIpcClient _discordClient;
		private readonly ILogger _logger;

		public Executor(DiscordIpcClient discordClient, ILogger logger)
		{
			_discordClient = discordClient;
			_logger = logger;
		}

		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			if (!_discordClient.IsConnected || !_discordClient.IsAuthenticated)
			{
				return ActionResult.Failed(
					ActionErrorCodes.NotConnected,
					LocalizedText.FromLiteral("Discord is not connected or authenticated. Please verify your plugin settings."));
			}

			var sounds = _discordClient.Sounds;
			if (sounds.Count == 0)
			{
				_discordClient.LoadCachedSounds();
				sounds = _discordClient.Sounds;
			}

			if (sounds.Count == 0)
			{
				return ActionResult.Failed(
					ActionErrorCodes.ProviderError,
					LocalizedText.FromLiteral("No soundboard sounds available."));
			}

			var poolSelection = "all";
			if (context.Parameters.TryGetValue(PoolParameter, out var poolVal) && poolVal != null)
			{
				var str = poolVal.ToString()?.Trim();
				if (!string.IsNullOrEmpty(str)) poolSelection = str;
			}

			List<DiscordSound> candidates;

			if (poolSelection.Equals("favorites", StringComparison.OrdinalIgnoreCase))
			{
				HashSet<string> favorites = [];
				try
				{
					var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
					var favPath = Path.Combine(appData, "MacroDeck", "plugins", "com.sertaycil.discord-soundboard", "favorites.json");
					if (File.Exists(favPath))
					{
						var json = await File.ReadAllTextAsync(favPath, context.CancellationToken);
						var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
						if (list != null) favorites = new HashSet<string>(list);
					}
				}
				catch { }

				candidates = sounds.Where(s => favorites.Contains(s.SoundId)).ToList();
				if (candidates.Count == 0)
				{
					candidates = sounds.ToList();
				}
			}
			else if (poolSelection.StartsWith("guild:", StringComparison.OrdinalIgnoreCase))
			{
				var guildId = poolSelection["guild:".Length..];
				candidates = sounds.Where(s => s.GuildId == guildId).ToList();
				if (candidates.Count == 0) candidates = sounds.ToList();
			}
			else
			{
				candidates = sounds.ToList();
			}

			var chosen = candidates[Random.Shared.Next(candidates.Count)];

			double? volumeScale = null;
			if (context.Parameters.TryGetValue(VolumeParameter, out var volObj) && volObj != null)
			{
				if (double.TryParse(volObj.ToString(), out var volNum))
				{
					volumeScale = Math.Clamp(volNum / 100.0, 0.0, 1.0);
				}
			}

			_logger.Information("PlayRandomSoundAction playing random sound {Name} ({SoundId}) from pool {Pool}", chosen.Name, chosen.SoundId, poolSelection);

			var success = await _discordClient.PlaySoundAsync(chosen.SoundId, chosen.GuildId, volumeScale, context.CancellationToken);
			if (!success)
			{
				return ActionResult.Failed(
					ActionErrorCodes.ProviderError,
					LocalizedText.FromLiteral("Failed to play soundboard sound in Discord. Make sure you are in a voice channel."));
			}

			return await ActionResult.SucceededTask;
		}
	}
}

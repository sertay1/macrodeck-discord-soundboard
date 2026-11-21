using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Serilog;

namespace DiscordSoundboard;

public sealed class PlaySoundAction : IActionDefinition, IDynamicOptionsActionDefinition, IIconProviderActionDefinition
{
	private const string SoundParameter = "soundId";

	private readonly DiscordIpcClient _discordClient;
	private readonly ILogger _logger;

	public PlaySoundAction(DiscordIpcClient discordClient, ILogger logger)
	{
		_discordClient = discordClient;
		_logger = logger.ForContext<PlaySoundAction>();

		Parameters =
		[
			ActionParameter.DynamicChoice(
				SoundParameter,
				label: Strings.Actions.PlaySound.Sound.Label(),
				description: Strings.Actions.PlaySound.Sound.Description(),
				optionsSourceId: null!,
				placeholder: Strings.Actions.PlaySound.Sound.Placeholder(),
				required: true)
		];
	}

	public string Id => "play-sound";

	public LocalizedText Name => Strings.Actions.PlaySound.Name();

	public LocalizedText Description => Strings.Actions.PlaySound.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; }

	public MacroDeckPlatform Platforms => MacroDeckPlatform.All;

	public TimeSpan IconPollInterval => TimeSpan.Zero;

	public Task<ActionIconSnapshot?> GetActionIconAsync(IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
	{
		if (parameters.TryGetValue(SoundParameter, out var val) && val != null)
		{
			var soundId = val.ToString();
			if (!string.IsNullOrWhiteSpace(soundId))
			{
				var sound = _discordClient.Sounds.FirstOrDefault(s => s.SoundId == soundId);
				var version = sound?.EmojiId ?? sound?.EmojiName ?? soundId;
				return Task.FromResult<ActionIconSnapshot?>(new ActionIconSnapshot
				{
					Version = version,
					MediaType = "image/png"
				});
			}
		}

		return Task.FromResult<ActionIconSnapshot?>(new ActionIconSnapshot
		{
			NoIcon = true
		});
	}

	public async Task<ActionIconContent?> GetActionIconContentAsync(IReadOnlyDictionary<string, object?> parameters, string version, CancellationToken cancellationToken)
	{
		if (parameters.TryGetValue(SoundParameter, out var val) && val != null)
		{
			var soundId = val.ToString();
			if (!string.IsNullOrWhiteSpace(soundId))
			{
				var sound = _discordClient.Sounds.FirstOrDefault(s => s.SoundId == soundId)
					?? new DiscordSound { SoundId = soundId, Name = "Sound" };

				if (OperatingSystem.IsWindows())
				{
					var bytes = await IconHelper.GetIconPngBytesAsync(sound, _discordClient.HttpClient, cancellationToken);
					if (bytes.Length > 0)
					{
						return new ActionIconContent(bytes, "image/png");
					}
				}
			}
		}

		return null;
	}

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

		var options = sounds
			.OrderBy(s => string.IsNullOrEmpty(s.GuildName) || s.GuildId == "0" ? " 0_Discord" : s.GuildName)
			.ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
			.Select(s =>
			{
				var isDefault = string.IsNullOrEmpty(s.GuildName) || s.GuildId == "0";
				var guildPart = isDefault ? "[Discord] " : $"[{s.GuildName}] ";
				var emojiPart = !string.IsNullOrEmpty(s.EmojiName) ? $"{s.EmojiName} " : "";
				return new ActionParameterOption
				{
					Value = s.SoundId,
					Label = LocalizedText.FromLiteral($"{guildPart}{emojiPart}{s.Name}")
				};
			})
			.ToList();

		if (options.Count == 0)
		{
			options.Add(new ActionParameterOption
			{
				Value = "",
				Label = LocalizedText.FromLiteral("Henüz ses yüklenmedi (Discord bağlantısını kontrol edin)")
			});
		}

		return new DynamicOptionsResult
		{
			Options = options,
			AllowsCustomValue = true
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
			if (!context.Parameters.TryGetValue(SoundParameter, out var val) || val == null)
			{
				return ActionResult.Failed(
					ActionErrorCodes.InvalidParameter,
					LocalizedText.FromLiteral("No sound selected."));
			}

			var soundId = val.ToString();
			if (string.IsNullOrWhiteSpace(soundId))
			{
				return ActionResult.Failed(
					ActionErrorCodes.InvalidParameter,
					LocalizedText.FromLiteral("Sound ID cannot be empty."));
			}

			if (!_discordClient.IsConnected || !_discordClient.IsAuthenticated)
			{
				return ActionResult.Failed(
					ActionErrorCodes.NotConnected,
					LocalizedText.FromLiteral("Discord is not connected or authenticated. Please verify your plugin settings."));
			}

			var sound = _discordClient.Sounds.FirstOrDefault(s => s.SoundId == soundId);
			var guildId = sound?.GuildId;

			var success = await _discordClient.PlaySoundAsync(soundId, guildId, context.CancellationToken);
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

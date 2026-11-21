using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using Serilog;

namespace DiscordSoundboard;

public sealed class PluginIntegration : IPluginIntegration, IConfigFlowProvider
{
	private readonly ILogger _logger;
	private readonly DiscordIpcClient _discordClient;
	private readonly SoundboardManagerServer _managerServer;

	public PluginIntegration(ILogger logger, DiscordIpcClient discordClient, SoundboardManagerServer managerServer)
	{
		_logger = logger.ForContext<PluginIntegration>();
		_discordClient = discordClient;
		_managerServer = managerServer;
		Actions =
		[
			new PlaySoundAction(discordClient, logger),
			new OpenManagerAction(managerServer, logger)
		];
	}

	public IReadOnlyList<IActionDefinition> Actions { get; }

	public bool AllowsMultipleConfigurations => false;

	public bool RequiresConfiguration => true;

	public IConfigFlow CreateConfigFlow() => new DiscordConfigFlow(_discordClient, _logger);

	public async Task InitializeAsync(IIntegrationContext context)
	{
		_logger.Information("Initializing Discord Soundboard integration...");

		// Note: SoundboardManagerServer starts on-demand when the user opens the Manager window,
		// so no background server runs unnecessarily when playing sounds.

		var entries = await context.Config.GetEntriesAsync(CancellationToken.None);
		if (entries.Count > 0)
		{
			var entryId = entries[0].Id;
			var clientId = await context.Config.GetStringAsync(entryId, "clientId", CancellationToken.None);
			var clientSecret = await context.Config.GetSecretAsync(entryId, "clientSecret", CancellationToken.None);
			var accessToken = await context.Config.GetSecretAsync(entryId, "accessToken", CancellationToken.None);

			if (!string.IsNullOrEmpty(clientId))
			{
				_discordClient.ClientId = clientId;
				_discordClient.ClientSecret = clientSecret ?? string.Empty;
				_discordClient.AccessToken = accessToken ?? string.Empty;

				_ = _discordClient.ConnectAsync(clientId, CancellationToken.None);
			}
		}

		_logger.Information("Discord Soundboard initialized.");
	}

	public async Task ShutdownAsync()
	{
		_logger.Information("Shutting down Discord Soundboard integration...");
		await _managerServer.DisposeAsync();
		await _discordClient.DisconnectAsync();
	}
}

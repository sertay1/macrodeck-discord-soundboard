using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Serilog;

namespace DiscordSoundboard;

public sealed class OpenManagerAction : IActionDefinition
{
	private readonly SoundboardManagerServer _server;
	private readonly ILogger _logger;

	public OpenManagerAction(SoundboardManagerServer server, ILogger logger)
	{
		_server = server;
		_logger = logger.ForContext<OpenManagerAction>();
	}

	public string Id => "open-manager";

	public LocalizedText Name => LocalizedText.FromLiteral("Open Soundboard Manager");

	public LocalizedText Description => LocalizedText.FromLiteral("Opens the visual soundboard browser with sound search, audio preview, and server filtering.");

	public IReadOnlyList<ActionParameter> Parameters => [];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.All;

	public IActionExecutor CreateExecutor() => new Executor(_server, _logger);

	private sealed class Executor : IActionExecutor
	{
		private readonly SoundboardManagerServer _server;
		private readonly ILogger _logger;

		public Executor(SoundboardManagerServer server, ILogger logger)
		{
			_server = server;
			_logger = logger;
		}

		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			try
			{
				_server.OpenWindow(context.OwnerWidgetId);
				return await ActionResult.SucceededTask;
			}
			catch (Exception ex)
			{
				_logger.Error(ex, "Failed to open manager window");
				return ActionResult.Failed(ActionErrorCodes.ProviderError, LocalizedText.FromLiteral("Could not open Soundboard Manager."));
			}
		}
	}
}

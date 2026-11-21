using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using Serilog;

namespace DiscordSoundboard;

public sealed class DiscordConfigFlow : IConfigFlow
{
	private const string StepCredentials = "credentials";
	private const string FieldClientId = "clientId";
	private const string FieldClientSecret = "clientSecret";

	private readonly DiscordIpcClient _discordClient;
	private readonly ILogger _logger;

	public DiscordConfigFlow(DiscordIpcClient discordClient, ILogger logger)
	{
		_discordClient = discordClient;
		_logger = logger.ForContext<DiscordConfigFlow>();
	}

	public Task<ConfigFlowResult> StartAsync(IConfigFlowContext context, CancellationToken cancellationToken)
	{
		return Task.FromResult(ConfigFlowResult.Step(CreateCredentialsStep()));
	}

	public async Task<ConfigFlowResult> SubmitAsync(
		string stepId,
		IReadOnlyDictionary<string, object?> input,
		IConfigFlowContext context,
		CancellationToken cancellationToken)
	{
		if (stepId != StepCredentials)
		{
			return ConfigFlowResult.Step(CreateCredentialsStep());
		}

		var clientId = input.TryGetValue(FieldClientId, out var idObj) ? idObj?.ToString()?.Trim() : null;
		var clientSecret = input.TryGetValue(FieldClientSecret, out var secObj) ? secObj?.ToString()?.Trim() : null;

		var fieldErrors = new Dictionary<string, LocalizedText>();
		if (string.IsNullOrWhiteSpace(clientId))
		{
			fieldErrors[FieldClientId] = LocalizedText.FromLiteral("Client ID is required.");
		}
		if (string.IsNullOrWhiteSpace(clientSecret))
		{
			fieldErrors[FieldClientSecret] = LocalizedText.FromLiteral("Client Secret is required.");
		}

		if (fieldErrors.Count > 0)
		{
			return ConfigFlowResult.Error(
				CreateCredentialsStep(),
				LocalizedText.FromLiteral("Please fill in all required fields."),
				fieldErrors);
		}

		_logger.Information("Connecting to Discord with Client ID: {ClientId}...", clientId);

		var authorized = await _discordClient.AuthorizeAndAuthenticateAsync(clientId!, clientSecret!, cancellationToken);
		if (!authorized)
		{
			return ConfigFlowResult.Error(
				CreateCredentialsStep(),
				LocalizedText.FromLiteral("Discord authorization failed. Make sure Discord desktop is running, your Client Secret is correct, and approve the authorization prompt inside Discord."),
				new Dictionary<string, LocalizedText>());
		}

		_logger.Information("Discord authorized successfully for user {User}", _discordClient.CurrentUsername);

		await _discordClient.FetchSoundsAsync(cancellationToken);

		var values = new Dictionary<string, ConfigFlowValue>
		{
			[FieldClientId] = ConfigFlowValue.Plain(clientId!),
			[FieldClientSecret] = ConfigFlowValue.Secret(clientSecret!),
			["accessToken"] = ConfigFlowValue.Secret(_discordClient.AccessToken),
			["refreshToken"] = ConfigFlowValue.Secret(_discordClient.RefreshToken)
		};

		var title = !string.IsNullOrEmpty(_discordClient.CurrentUsername)
			? $"Discord ({_discordClient.CurrentUsername})"
			: "Discord Account";

		return ConfigFlowResult.Complete(title, values);
	}

	private static ConfigFlowStep CreateCredentialsStep()
	{
		return new ConfigFlowStep
		{
			StepId = StepCredentials,
			Title = Strings.ConfigFlow.Step.Title(),
			Description = Strings.ConfigFlow.Step.Description(),
			Instructions =
			[
				new ConfigFlowInstruction
				{
					Text = Strings.ConfigFlow.Instruction()
				}
			],
			Links =
			[
				new ConfigFlowLink
				{
					Label = LocalizedText.FromLiteral("Discord Developer Portal"),
					Url = "https://discord.com/developers/applications"
				}
			],
			Fields =
			[
				ActionParameter.Text(
					FieldClientId,
					label: Strings.ConfigFlow.ClientId.Label(),
					description: Strings.ConfigFlow.ClientId.Description(),
					placeholder: Strings.ConfigFlow.ClientId.Placeholder(),
					required: true),
				ActionParameter.Secret(
					FieldClientSecret,
					label: Strings.ConfigFlow.ClientSecret.Label(),
					description: Strings.ConfigFlow.ClientSecret.Description(),
					required: true)
			]
		};
	}
}

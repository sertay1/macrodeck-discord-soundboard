using Microsoft.Extensions.DependencyInjection;
using MacroDeck.Plugin.Testing;
using MacroDeck.Sdk.Actions;
using NUnit.Framework;

namespace DiscordSoundboard.Tests;

[TestFixture]
public sealed class PluginIntegrationTests
{
	private static PluginTestHarness CreateHarness() =>
		PluginTestHarness.Create(builder => builder
			.UseLocalization(Strings.LocalizationCatalog)
			.ConfigureServices((_, services) =>
			{
				services.AddSingleton<DiscordIpcClient>();
				services.AddSingleton<SoundboardManagerServer>();
			})
			.RegisterIntegration<PluginIntegration>());

	[Test]
	public void Inspect_ActionExecutionContext()
	{
		var t = typeof(MacroDeck.Sdk.Actions.ActionExecutionContext);
		foreach (var p in t.GetProperties())
		{
			TestContext.Out.WriteLine($"Prop: {p.PropertyType.FullName} {p.Name}");
		}
	}

	[Test]
	public async Task The_dynamic_options_returns_sounds()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		var client = harness.Services.GetRequiredService<DiscordIpcClient>();
		client.Sounds.Add(new DiscordSound { SoundId = "123", Name = "Airhorn", EmojiName = "📢" });

		var dynamicAction = new PlaySoundAction(client, Serilog.Log.Logger);
		var result = await dynamicAction.GetDynamicOptionsAsync(
			new DynamicOptionsContext
			{
				ParameterName = "soundId",
				CurrentParameters = new Dictionary<string, object?>()
			},
			CancellationToken.None);

		Assert.That(result.Options, Is.Not.Empty);
		Assert.That(result.Options.Any(o => o.Value == "123"), Is.True);
	}

	[Test]
	public async Task The_plugin_builds_and_initializes()
	{
		await using var harness = CreateHarness();
		Assert.DoesNotThrowAsync(harness.InitializeIntegrationsAsync);
	}

	[Test]
	public async Task The_play_sound_action_fails_when_no_sound_selected()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		var outcome = await harness.Actions.ExecuteAsync(
			"play-sound",
			new Dictionary<string, object?> { ["soundId"] = "" });

		Assert.That(outcome.Succeeded, Is.False);
	}

	[Test]
	public async Task The_play_sound_action_fails_when_not_connected()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		var outcome = await harness.Actions.ExecuteAsync(
			"play-sound",
			new Dictionary<string, object?> { ["soundId"] = "12345" });

		Assert.That(outcome.Succeeded, Is.False);
	}

	[Test]
	public async Task The_config_flow_starts_with_credentials_step()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		var flow = harness.Services.GetRequiredService<PluginIntegration>().CreateConfigFlow();
		var stepResult = await flow.StartAsync(null!, CancellationToken.None);

		Assert.That(stepResult.Kind, Is.EqualTo(MacroDeck.Sdk.ConfigFlow.ConfigFlowResultKind.Step));
		Assert.That(stepResult.NextStep?.StepId, Is.EqualTo("credentials"));
	}
}

[TestFixture]
public sealed class LocalizationTests
{
	[Test]
	public void The_catalog_is_scoped_to_the_plugin_id()
	{
		Assert.That(Strings.LocalizationCatalog.Scope, Is.EqualTo("plugin:com.sertaycil.discord-soundboard"));
	}

	[Test]
	public void English_is_the_default_culture()
	{
		Assert.That(Strings.LocalizationCatalog.DefaultCulture, Is.EqualTo("en"));
		Assert.That(Strings.LocalizationCatalog.Cultures, Does.Contain("en"));
	}

	[Test]
	public void The_action_strings_come_from_the_catalog()
	{
		Assert.That(Strings.LocalizationCatalog.KeysOf("en"), Does.Contain("Actions.PlaySound.Name"));
	}

	[Test]
	public void Every_key_the_default_culture_declares_resolves_to_text()
	{
		foreach (var key in Strings.LocalizationCatalog.KeysOf("en"))
		{
			Assert.That(Strings.LocalizationCatalog.TryGetTemplate("en", key, out var text), Is.True);
			Assert.That(text, Is.Not.Empty);
		}
	}
}

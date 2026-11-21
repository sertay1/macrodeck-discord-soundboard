using Microsoft.Extensions.DependencyInjection;
using MacroDeck.Plugin.Hosting;
using MacroDeck.Plugin.Serilog;
using DiscordSoundboard;

var plugin = MacroDeckPlugin.CreatePlugin(args)
	.UseMacroDeckLogging()
	.UseLocalization(Strings.LocalizationCatalog)
	.ConfigureServices((_, services) =>
	{
		services.AddSingleton<DiscordIpcClient>();
		services.AddSingleton<SoundboardManagerServer>();
	})
	.RegisterIntegration<PluginIntegration>()
	.Build();

await plugin.RunAsync();

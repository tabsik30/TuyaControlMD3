using MacroDeck.Plugin.Hosting;
using MacroDeck.Plugin.Testing;
using NUnit.Framework;

namespace TuyaControl.Tests;

/// <summary>
/// Behaviour tests through <see cref="PluginTestHarness"/>: the plugin's own capability handlers run,
/// but nothing crosses a socket. This is where you test what your integration does.
/// </summary>
[TestFixture]
public sealed class PluginIntegrationTests
{
	[Test]
	public async Task The_plugin_builds_and_initializes()
	{
		await using var harness = PluginTestHarness.Create(builder => builder.RegisterIntegration<PluginIntegration>());

		Assert.DoesNotThrowAsync(harness.InitializeIntegrationsAsync);
	}
}

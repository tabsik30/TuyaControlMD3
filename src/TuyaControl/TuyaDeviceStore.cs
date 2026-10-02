using System.Collections.Concurrent;
using MacroDeck.Sdk;
using TuyaControl.Models;

namespace TuyaControl.Services;

/// <summary>
/// Each device is one MD3 config-flow entry (kind="device") in the host's config
/// store, the cloud account is one entry (kind="account") - see
/// ConfigFlow/TuyaConfigFlow.cs for where entries are written, and
/// PluginIntegration.InitializeAsync for where they're loaded back via
/// LoadFromHostAsync. The host's config store is the source of truth.
///
/// State synchronization (a per-device "on/off" variable kept fresh by a polling
/// BackgroundService) was removed on request - this plugin no longer implements
/// IVariableProvider at all, matching the original MD2 plugin's design (actions
/// only). Without a declared variable list, there's no "declare capabilities before
/// devices are loaded" timing problem either - GetDynamicOptionsAsync on each action
/// resolves the device list on demand, well after InitializeAsync has already run.
/// </summary>
public sealed class TuyaDeviceStore
{
    private readonly ConcurrentDictionary<string, TuyaDeviceRecord> _devices = new(StringComparer.Ordinal);

    public TuyaRegion? AccountRegion { get; private set; }
    public string? AccountCountryCode { get; private set; }
    public string? AccountUsername { get; private set; }
    public string? AccountPassword { get; private set; }
    public string AccountSchema { get; private set; } = "tuyaSmart";
    public string? AccountClientId { get; private set; }
    public string? AccountClientSecret { get; private set; }

    public bool HasAccount => AccountUsername is not null;

    public IReadOnlyList<TuyaDeviceRecord> Devices => [.. _devices.Values];

    public TuyaDeviceRecord? GetById(string deviceId) =>
        _devices.TryGetValue(deviceId, out var device) ? device : null;

    public void SetDevice(TuyaDeviceRecord device) => _devices[device.Id] = device;

    public void SetAccount(TuyaRegion region, string countryCode, string username, string password, string schema, string clientId, string clientSecret)
    {
        AccountRegion = region;
        AccountCountryCode = countryCode;
        AccountUsername = username;
        AccountPassword = password;
        AccountSchema = schema;
        AccountClientId = clientId;
        AccountClientSecret = clientSecret;
    }

    /// <summary>
    /// Reads every config entry the host has stored for this plugin and rebuilds the
    /// in-memory device list + account settings. Call once from
    /// PluginIntegration.InitializeAsync.
    /// </summary>
    public async Task LoadFromHostAsync(IIntegrationContext context)
    {
        var entries = await context.Config.GetEntriesAsync();
        foreach (var entry in entries)
        {
            var kind = await context.Config.GetStringAsync(entry.Id, "kind");
            if (kind == "device")
            {
                var id = await context.Config.GetStringAsync(entry.Id, "id");
                var name = await context.Config.GetStringAsync(entry.Id, "name");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var localKey = await context.Config.GetSecretAsync(entry.Id, "localKey");
                var ip = await context.Config.GetStringAsync(entry.Id, "ipAddress") ?? "";
                var protocolVersion = await context.Config.GetStringAsync(entry.Id, "protocolVersion") ?? "3.3";

                SetDevice(new TuyaDeviceRecord
                {
                    Id = id,
                    Name = name,
                    LocalKey = localKey,
                    IpAddress = ip,
                    ProtocolVersion = protocolVersion,
                });
            }
            else if (kind == "account")
            {
                var regionText = await context.Config.GetStringAsync(entry.Id, "region");
                var countryCode = await context.Config.GetStringAsync(entry.Id, "countryCode");
                var username = await context.Config.GetStringAsync(entry.Id, "username");
                var password = await context.Config.GetSecretAsync(entry.Id, "password");
                var schema = await context.Config.GetStringAsync(entry.Id, "schema");
                var clientId = await context.Config.GetStringAsync(entry.Id, "clientId");
                var clientSecret = await context.Config.GetSecretAsync(entry.Id, "clientSecret");

                if (Enum.TryParse<TuyaRegion>(regionText, out var region) &&
                    !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password) &&
                    !string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret))
                {
                    SetAccount(region, countryCode ?? "48", username, password, schema ?? "tuyaSmart", clientId, clientSecret);
                }
            }
        }
    }
}

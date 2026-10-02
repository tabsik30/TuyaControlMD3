using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using TuyaControl.Models;
using TuyaControl.Services;

namespace TuyaControl.ConfigFlow;

/// <summary>
/// Single config flow covering two very different jobs the MD2 plugin split across
/// two WinForms windows (LoginForm, DeviceManagerForm):
///
///   kind (account/device)
///     -> account: one step (region/country/username/password/schema), logs in and
///        caches the cloud device list in TuyaCloudClient for the "device" branch
///     -> device:
///          -> source (cloud/manual) - "cloud" only offered if a cloud session with
///             cached devices already exists in this process
///          -> cloud-pick: choose from TuyaCloudClient.CachedDevices, auto-fills
///             id/name/local key
///          -> manual: type id/name/local key by hand
///          -> network (always): ip address + protocol version
///          -> Complete
///
/// Each step's fields are built with ordinary C#, not IDynamicOptionsActionDefinition -
/// the config-flow capability has no separate "options" operation the way actions do
/// (confirmed from the decompiled ConfigFlowCapabilityHandler), so anything "dynamic"
/// has to be computed synchronously inside StartAsync/SubmitAsync instead.
/// </summary>
internal sealed class TuyaConfigFlow(TuyaCloudClient cloudClient, TuyaDeviceStore store) : IConfigFlow
{
    private string? _deviceId;
    private string? _deviceName;
    private string? _localKey;

    public Task<ConfigFlowResult> StartAsync(IConfigFlowContext context, CancellationToken cancellationToken) =>
        Task.FromResult(ConfigFlowResult.Step(BuildKindStep()));

    public Task<ConfigFlowResult> SubmitAsync(string stepId, IReadOnlyDictionary<string, object?> input, IConfigFlowContext context, CancellationToken cancellationToken)
    {
        return stepId switch
        {
            "kind" => Task.FromResult(HandleKind(input)),
            "account" => HandleAccountAsync(input),
            "device-source" => Task.FromResult(HandleDeviceSource(input)),
            "device-cloud-pick" => HandleCloudPickAsync(input),
            "device-manual" => Task.FromResult(HandleManual(input)),
            "device-network" => Task.FromResult(HandleNetwork(input)),
            _ => Task.FromResult(ConfigFlowResult.Error(BuildKindStep(), "Unknown step.")),
        };
    }

    // --- kind ---

    private static ConfigFlowStep BuildKindStep() => new()
    {
        StepId = "kind",
        Title = "What are you setting up?",
        Fields =
        [
            ActionParameter.Choice("kind",
                [
                    new ActionParameterOption { Value = "account", Label = "Tuya cloud account (login)" },
                    new ActionParameterOption { Value = "device", Label = "A device" },
                ],
                label: "Type", required: true),
        ],
    };

    private ConfigFlowResult HandleKind(IReadOnlyDictionary<string, object?> input)
    {
        var kind = input.GetValueOrDefault("kind")?.ToString();
        return kind switch
        {
            "account" => ConfigFlowResult.Step(BuildAccountStep()),
            "device" => ConfigFlowResult.Step(BuildDeviceSourceOrManualStep()),
            _ => ConfigFlowResult.Error(BuildKindStep(), "Pick one of the options."),
        };
    }

    // --- account ---

    private static ConfigFlowStep BuildAccountStep() => new()
    {
        StepId = "account",
        Title = "Tuya cloud account",
        Description = "Account credentials are the same as the Tuya Smart / Smart Life app. " +
                      "Client ID/Secret come from your own Tuya IoT Platform \"Cloud\" project " +
                      "(iot.tuya.com -> Cloud -> Development -> your project -> Overview).",
        Fields =
        [
            ActionParameter.Choice("region",
                Enum.GetValues<TuyaRegion>().Select(r => new ActionParameterOption { Value = r.ToString(), Label = r.ToString() }).ToList(),
                label: "Region", defaultValue: TuyaRegion.CentralEurope.ToString(), required: true),
            ActionParameter.Text("countryCode", label: "Country code", defaultValue: "48", required: true),
            ActionParameter.Text("username", label: "Username / email", required: true),
            ActionParameter.Password("password", label: "Password", required: true),
            ActionParameter.Choice("schema",
                [
                    new ActionParameterOption { Value = "tuyaSmart", Label = "Tuya Smart" },
                    new ActionParameterOption { Value = "smartlife", Label = "Smart Life" },
                ],
                label: "App", defaultValue: "tuyaSmart", required: true),
            ActionParameter.Text("client_id", label: "Client ID (Access ID)", required: true),
            ActionParameter.Password("client_secret", label: "Client Secret (Access Secret)", required: true),
        ],
    };

    private async Task<ConfigFlowResult> HandleAccountAsync(IReadOnlyDictionary<string, object?> input)
    {
        var regionText = input.GetValueOrDefault("region")?.ToString();
        var countryCode = input.GetValueOrDefault("countryCode")?.ToString() ?? "48";
        var username = input.GetValueOrDefault("username")?.ToString();
        var password = input.GetValueOrDefault("password")?.ToString();
        var schema = input.GetValueOrDefault("schema")?.ToString() ?? "tuyaSmart";
        var clientId = input.GetValueOrDefault("client_id")?.ToString();
        var clientSecret = input.GetValueOrDefault("client_secret")?.ToString();

        if (!Enum.TryParse<TuyaRegion>(regionText, out var region) || string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return ConfigFlowResult.Error(BuildAccountStep(), "Fill in all the fields.");
        }

        cloudClient.Configure(region, clientId, clientSecret);
        cloudClient.CountryCode = countryCode;
        cloudClient.Username = username;
        cloudClient.Password = password;
        cloudClient.Schema = schema;

        try
        {
            await cloudClient.LoginAsync();
            await cloudClient.GetDevicesAsync();
        }
        catch (TuyaApiException ex)
        {
            return ConfigFlowResult.Error(BuildAccountStep(), ex.Message);
        }

        store.SetAccount(region, countryCode, username, password, schema, clientId, clientSecret);

        var values = new Dictionary<string, ConfigFlowValue>
        {
            ["kind"] = ConfigFlowValue.Plain("account"),
            ["region"] = ConfigFlowValue.Plain(region.ToString()),
            ["countryCode"] = ConfigFlowValue.Plain(countryCode),
            ["username"] = ConfigFlowValue.Plain(username),
            ["password"] = ConfigFlowValue.Secret(password),
            ["schema"] = ConfigFlowValue.Plain(schema),
            ["clientId"] = ConfigFlowValue.Plain(clientId),
            ["clientSecret"] = ConfigFlowValue.Secret(clientSecret),
        };

        return ConfigFlowResult.Complete("Tuya cloud account", values);
    }

    // --- device: source / cloud-pick / manual ---

    private ConfigFlowStep BuildDeviceSourceOrManualStep()
    {
        // Only offer "pick from cloud" if this process already has a logged-in
        // session with a cached device list (from the account step, this run).
        if (cloudClient.IsLoggedIn && cloudClient.CachedDevices.Count > 0)
        {
            return new ConfigFlowStep
            {
                StepId = "device-source",
                Title = "Add a device",
                Fields =
                [
                    ActionParameter.Choice("source",
                        [
                            new ActionParameterOption { Value = "cloud", Label = "Pick from the Tuya cloud" },
                            new ActionParameterOption { Value = "manual", Label = "Enter manually" },
                        ],
                        label: "Source", required: true),
                ],
            };
        }

        return BuildManualStep();
    }

    private ConfigFlowResult HandleDeviceSource(IReadOnlyDictionary<string, object?> input)
    {
        var source = input.GetValueOrDefault("source")?.ToString();
        return source switch
        {
            "cloud" => ConfigFlowResult.Step(BuildCloudPickStep(cloudClient)),
            "manual" => ConfigFlowResult.Step(BuildManualStep()),
            _ => ConfigFlowResult.Error(BuildDeviceSourceOrManualStep(), "Pick one of the options."),
        };
    }

    private static ConfigFlowStep BuildCloudPickStep(TuyaCloudClient cloud) => new()
    {
        StepId = "device-cloud-pick",
        Title = "Pick a device",
        Fields =
        [
            ActionParameter.Choice("cloud_device_id",
                cloud.CachedDevices.Select(d => new ActionParameterOption { Value = d.Id, Label = d.ToString() }).ToList(),
                label: "Device", required: true),
        ],
    };

    private static ConfigFlowStep BuildManualStep() => new()
    {
        StepId = "device-manual",
        Title = "Device details",
        Fields =
        [
            ActionParameter.Text("device_id", label: "Device ID", required: true),
            ActionParameter.Text("device_name", label: "Name", required: true),
            ActionParameter.Text("local_key", label: "Local key", description: "Leave empty if you'll only control this device via the cloud."),
        ],
    };

    private async Task<ConfigFlowResult> HandleCloudPickAsync(IReadOnlyDictionary<string, object?> input)
    {
        var deviceId = input.GetValueOrDefault("cloud_device_id")?.ToString();
        var picked = cloudClient.CachedDevices.FirstOrDefault(d => d.Id == deviceId);
        if (picked is null)
        {
            return ConfigFlowResult.Error(BuildCloudPickStep(cloudClient), "Pick a device from the list.");
        }

        _deviceId = picked.Id;
        _deviceName = picked.Name;
        try
        {
            _localKey = await cloudClient.GetLocalKeyAsync(picked.Id);
        }
        catch (TuyaApiException)
        {
            _localKey = null; // best-effort - the network step still lets them proceed
        }

        return ConfigFlowResult.Step(BuildNetworkStep());
    }

    private ConfigFlowResult HandleManual(IReadOnlyDictionary<string, object?> input)
    {
        var id = input.GetValueOrDefault("device_id")?.ToString();
        var name = input.GetValueOrDefault("device_name")?.ToString();
        var localKey = input.GetValueOrDefault("local_key")?.ToString();

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
        {
            return ConfigFlowResult.Error(BuildManualStep(), "Device ID and name are required.");
        }

        _deviceId = id;
        _deviceName = name;
        _localKey = string.IsNullOrWhiteSpace(localKey) ? null : localKey;

        return ConfigFlowResult.Step(BuildNetworkStep());
    }

    // --- device: network (always reached last) ---

    private static ConfigFlowStep BuildNetworkStep() => new()
    {
        StepId = "device-network",
        Title = "Network",
        Description = "Not needed if you'll control this device via the cloud (protocol \"cloud\").",
        Fields =
        [
            ActionParameter.Text("ip_address", label: "IP address"),
            ActionParameter.Choice("protocol_version",
                [
                    new ActionParameterOption { Value = "3.3", Label = "3.3" },
                    new ActionParameterOption { Value = "3.4", Label = "3.4" },
                    new ActionParameterOption { Value = "3.5", Label = "3.5" },
                    new ActionParameterOption { Value = "cloud", Label = "Cloud (no LAN)" },
                ],
                label: "Protocol version", defaultValue: "3.3", required: true),
        ],
    };

    private ConfigFlowResult HandleNetwork(IReadOnlyDictionary<string, object?> input)
    {
        if (_deviceId is null || _deviceName is null)
        {
            return ConfigFlowResult.Error(BuildKindStep(), "Something went wrong - start over.");
        }

        var ip = input.GetValueOrDefault("ip_address")?.ToString() ?? "";
        var protocolVersion = input.GetValueOrDefault("protocol_version")?.ToString() ?? "3.3";

        var record = new TuyaDeviceRecord
        {
            Id = _deviceId,
            Name = _deviceName,
            LocalKey = _localKey,
            IpAddress = ip,
            ProtocolVersion = protocolVersion,
        };

        store.SetDevice(record);

        var values = new Dictionary<string, ConfigFlowValue>
        {
            ["kind"] = ConfigFlowValue.Plain("device"),
            ["id"] = ConfigFlowValue.Plain(record.Id),
            ["name"] = ConfigFlowValue.Plain(record.Name),
            ["ipAddress"] = ConfigFlowValue.Plain(record.IpAddress),
            ["protocolVersion"] = ConfigFlowValue.Plain(record.ProtocolVersion),
        };
        if (!string.IsNullOrEmpty(record.LocalKey))
        {
            values["localKey"] = ConfigFlowValue.Secret(record.LocalKey);
        }

        return ConfigFlowResult.Complete(record.Name, values);
    }
}

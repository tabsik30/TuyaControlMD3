# Tuya Control - Macro Deck 3 Plugin

A Macro Deck 3 out-of-process plugin for controlling Tuya smart home devices
over your local network (LAN) with optional cloud fallback. Built on the
Macro Deck 3 plugin SDK.

## What This Plugin Does

Tuya Control lets you create Macro Deck buttons that control Tuya-based smart
home devices (lights, switches, plugs, dimmers, and more) directly from your
Macro Deck setup. It communicates with devices on your local network using the
Tuya LAN protocol (versions 3.3, 3.4, and 3.5), with an optional Tuya cloud
connection for devices that are not on your local network.

### Features

- **Local LAN control** - Direct communication with Tuya devices on your network
  (protocol 3.3, 3.4, 3.5) with low latency
- **Cloud fallback** - Optional Tuya cloud account for controlling devices
  remotely or devices that do not support local control
- **Device discovery** - UDP beacon listener to find device IP addresses and
  protocol versions on your network
- **Multiple action types** - Toggle, set state, or send custom commands
- **Secure credential storage** - Cloud credentials are stored in Macro Deck's
  encrypted secret store
- **Configuration flow** - Guided setup through Macro Deck's config flow UI

## Requirements

- **Macro Deck 3** desktop app (version 3.0.0-beta.26 or later)
- **.NET SDK 10.0** (for building from source)
- Tuya smart home devices on your local network
- (Optional) Tuya IoT Platform account for cloud features

## Installation

### From Release Artifact

1. Download the latest `.macroDeckPlugin` artifact from the
   [Releases](https://github.com/tabsik12/TuyaControl/releases) page
2. Open Macro Deck 3
3. Go to **Plugins → Install from file** and select the downloaded artifact
4. The plugin will appear in your plugin list

### From Source

```bash
git clone https://github.com/tabsik12/TuyaControl.git
cd TuyaControl
dotnet build -c Release
macrodeck-plugin pack --source src/TuyaControl/bin/Release/net10.0
```

## Configuration

The plugin uses Macro Deck's config flow. You will need to configure:

### 1. Tuya Cloud Account (Optional)

Only needed if you want cloud fallback or cloud-only devices.

1. Go to [iot.tuya.com](https://iot.tuya.com) and create a Cloud project
2. Navigate to **Cloud → Development → Your Project → Overview**
3. Copy your **Access ID** (Client ID) and **Access Secret** (Client Secret)
4. In Macro Deck, open the Tuya Control config flow and select
   **Tuya cloud account (login)**
5. Fill in your region, country code, username, password, app type, and
   credentials

### 2. Add Devices

For each device you want to control:

1. Open the Tuya Control config flow and select **A device**
2. Choose a source:
   - **Pick from the Tuya cloud** - Select from your cloud device list
     (requires cloud account setup first)
   - **Enter manually** - Type the device ID, name, and local key yourself
3. Enter the network details:
   - **IP address** - The device's IP on your local network
   - **Protocol version** - 3.3, 3.4, 3.5, or "Cloud (no LAN)"

### Finding Your Device Information

- **Device ID** - Found in the Tuya Smart / Smart Life app under device settings
- **Local key** - Found in the Tuya IoT Platform under your cloud project's
  device list, or from the Tuya Smart app
- **IP address** - Your router's DHCP client list, or use the **Detect device
  (UDP)** action to find it
- **Protocol version** - Use the **Detect device (UDP)** action to detect it

## Available Actions

### Toggle Switch

Reads the current state of a switch DP and sends the opposite value (on to off,
or off to on).

| Parameter | Description |
|-----------|-------------|
| Device | Pick from your configured devices |
| DP code | The data point code (default: `switch_1`) |

### Set Switch State

Sends a fixed ON or OFF value for a DP code, regardless of the current state.

| Parameter | Description |
|-----------|-------------|
| Device | Pick from your configured devices |
| DP code | The data point code (default: `switch_1`) |
| State (on) | Toggle: true for ON, false for OFF |

### Send Custom Command

Sends any DP code and value pair. Useful for dimmers, color controls, modes,
or any DP not covered by the other actions.

| Parameter | Description |
|-----------|-------------|
| Device | Pick from your configured devices |
| DP code | The data point code to send |
| Value type | Boolean, Integer, or Text |
| Value | The value to send |

### Detect Device (UDP)

Listens for 15 seconds for a device's Tuya LAN broadcast beacon to discover its
current IP address and protocol version. Shows the result as a notification.

| Parameter | Description |
|-----------|-------------|
| Device | Pick from your configured devices |

## Variables

| Variable | Type | Description |
|----------|------|-------------|
| `tuya_state` | Boolean | The on/off state of the first non-cloud device (refreshes every 5 seconds) |

## Building from Source

### Prerequisites

- [.NET SDK 10.0](https://dotnet.microsoft.com/download)
- [Macro Deck Plugin CLI](https://github.com/Macro-Deck-App/Macro-Deck-3)
  (`dotnet tool install --global MacroDeck.Plugin.Cli --prerelease`)

### Build

```bash
dotnet build
```

### Test

```bash
dotnet test
```

### Pack

```bash
dotnet build -c Release
macrodeck-plugin pack --source src/TuyaControl/bin/Release/net10.0
```

### Conformance Test

```bash
macrodeck-plugin test --project src/TuyaControl --report markdown --output conformance.md
```

## Project Structure

```
src/TuyaControl/
  Program.cs                 Plugin entry point
  manifest.json              Plugin identity and metadata
  PluginIntegration.cs       Main integration class
  TuyaConfigFlow.cs          Configuration flow
  TuyaCloudClient.cs         Tuya cloud API client
  TuyaLocalClient.cs         Tuya LAN protocol client (3.3/3.4/3.5)
  TuyaLocalClientV35.cs      Protocol 3.5 specific implementation
  TuyaDiscovery.cs           UDP device discovery
  TuyaDeviceStore.cs         Device configuration storage
  TuyaModels.cs              Shared model types
  TuyaAppCredentials.cs      Cloud credential management
  Crc32.cs                   CRC32 implementation for LAN protocol
  ToggleSwitchAction.cs      Toggle switch action
  SetSwitchStateAction.cs    Set switch state action
  SendCustomCommandAction.cs Custom command action
  DetectDeviceAction.cs      UDP device detection action
  Assets/icon.svg            Plugin icon
tests/TuyaControl.Tests/
  PluginIntegrationTests.cs   Integration tests
```

## Debugging

Use the **Macro Deck - Real Host** launch profile in your IDE to debug the
plugin against a running Macro Deck instance. See the
[Macro Deck 3 plugin development docs](https://github.com/Macro-Deck-App/Macro-Deck-3/tree/main/docs/plugin-development)
for detailed instructions.

## Contributing

Contributions are welcome. Please open an issue or pull request on
[GitHub](https://github.com/tabsik12/TuyaControl).

## License

MIT - see [LICENSE](LICENSE). Copyright (c) 2026 Maciej Kaczmarek.

Macro Deck is licensed under Apache 2.0 and developed by
[Macro Deck App](https://github.com/Macro-Deck-App).

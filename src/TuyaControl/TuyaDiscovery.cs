using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Serilog;
using Newtonsoft.Json.Linq;

namespace TuyaControl.Services;

public sealed class TuyaDiscoveredDevice
{
    public required string GwId { get; init; }
    public required string Ip { get; init; }
    public required string Version { get; init; }
    public bool Encrypted { get; init; }
}

/// <summary>
/// Listens for Tuya's UDP broadcast beacons (ports 6666 plaintext / 6667 AES-ECB
/// encrypted with a fixed, publicly-known key) to discover a device's current IP and
/// protocol version without needing the cloud. Ported from TuyaDiscovery - used by
/// DetectDeviceAction as a diagnostic action (there's no device-manager grid in MD3
/// to trigger this from a UI button anymore).
/// </summary>
public static class TuyaDiscovery
{
    private static readonly byte[] UdpKey = MD5.HashData(Encoding.UTF8.GetBytes("yGAdlopoPVldABfn"));

    public static async Task<List<TuyaDiscoveredDevice>> ListenAsync(int seconds, ILogger logger)
    {
        var results = new Dictionary<string, TuyaDiscoveredDevice>();
        UdpClient? c6666 = null;
        UdpClient? c6667 = null;

        try
        {
            c6666 = new UdpClient(AddressFamily.InterNetwork);
            c6666.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            c6666.Client.Bind(new IPEndPoint(IPAddress.Any, 6666));
        }
        catch (Exception ex)
        {
            logger.Debug("[Discovery] could not open port 6666: {Message}", ex.Message);
        }

        try
        {
            c6667 = new UdpClient(AddressFamily.InterNetwork);
            c6667.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            c6667.Client.Bind(new IPEndPoint(IPAddress.Any, 6667));
        }
        catch (Exception ex)
        {
            logger.Debug("[Discovery] could not open port 6667: {Message}", ex.Message);
        }

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var tasks = new List<Task>();
        if (c6666 is not null)
        {
            tasks.Add(ListenLoop(c6666, encrypted: false, results, cts.Token, logger));
        }

        if (c6667 is not null)
        {
            tasks.Add(ListenLoop(c6667, encrypted: true, results, cts.Token, logger));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            c6666?.Dispose();
            c6667?.Dispose();
        }

        return [.. results.Values];
    }

    private static async Task ListenLoop(UdpClient client, bool encrypted, Dictionary<string, TuyaDiscoveredDevice> results, CancellationToken token, ILogger logger)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var receiveTask = client.ReceiveAsync(token).AsTask();
                var delayTask = Task.Delay(500, token);
                if (await Task.WhenAny(receiveTask, delayTask) == receiveTask)
                {
                    var result = receiveTask.Result;
                    ProcessPacket(result.Buffer, result.RemoteEndPoint.Address.ToString(), encrypted, results, logger);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.Debug("[Discovery] receive error: {Message}", ex.Message);
            }
        }
    }

    private static void ProcessPacket(byte[] data, string ip, bool encrypted, Dictionary<string, TuyaDiscoveredDevice> results, ILogger logger)
    {
        foreach (var headerLen in new[] { 20, 16 })
        {
            if (data.Length <= headerLen + 8)
            {
                continue;
            }

            var body = new byte[data.Length - headerLen - 8];
            Array.Copy(data, headerLen, body, 0, body.Length);
            var text = encrypted ? DecryptEcb(body, UdpKey) : SafeUtf8(body);
            if (text is null)
            {
                continue;
            }

            text = text.Trim().TrimEnd('\0');
            if (!text.StartsWith('{') || !text.EndsWith('}'))
            {
                continue;
            }

            try
            {
                var json = JObject.Parse(text);
                var gwId = json["gwId"]?.ToString() ?? "?";
                var version = json["version"]?.ToString() ?? "(no version field)";
                var deviceIp = json["ip"]?.ToString() ?? ip;
                results[gwId] = new TuyaDiscoveredDevice
                {
                    GwId = gwId,
                    Ip = deviceIp,
                    Version = version,
                    Encrypted = encrypted,
                };
                logger.Debug("[Discovery] recognized: gwId={GwId}, version={Version}, ip={Ip}", gwId, version, deviceIp);
                break;
            }
            catch
            {
                // not a JSON beacon, try the other header length
            }
        }
    }

    private static string? SafeUtf8(byte[] data)
    {
        try
        {
            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            return null;
        }
    }

    private static string? DecryptEcb(byte[] data, byte[] key)
    {
        if (data.Length == 0 || data.Length % 16 != 0)
        {
            return null;
        }

        try
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = key;
            using var dec = aes.CreateDecryptor();
            var raw = dec.TransformFinalBlock(data, 0, data.Length);

            int pad = raw[^1];
            if (pad > 0 && pad <= 16 && pad <= raw.Length)
            {
                var valid = true;
                for (var i = raw.Length - pad; i < raw.Length; i++)
                {
                    if (raw[i] != pad)
                    {
                        valid = false;
                        break;
                    }
                }

                if (valid)
                {
                    var trimmed = new byte[raw.Length - pad];
                    Array.Copy(raw, trimmed, trimmed.Length);
                    return Encoding.UTF8.GetString(trimmed);
                }
            }

            return Encoding.UTF8.GetString(raw);
        }
        catch
        {
            return null;
        }
    }
}

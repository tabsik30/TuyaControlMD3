using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Serilog;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TuyaControl.Models;

namespace TuyaControl.Services;

/// <summary>
/// Controls a device over the local Tuya LAN protocol (versions 3.3 and 3.4 handled
/// here directly; 3.5 delegates to TuyaLocalClientV35; "cloud" delegates to
/// TuyaCloudClient). Ported from TuyaLocalClient - packet framing, AES-ECB
/// encryption, 3.4's session-key handshake, and the CONTROL(7)/CONTROL_NEW(13)
/// fallback dance are all unchanged.
///
/// TuyaDebugLog.Write(...) calls from the original became ILogger.LogDebug here.
/// TuyaPluginInstance.MainInstance?.ApiClient became a constructor-injected
/// TuyaCloudClient (DI singleton), since MD3 has no static plugin-instance registry.
/// </summary>
public sealed class TuyaLocalClient(TuyaCloudClient cloudClient, ILogger logger)
{
    private readonly ILogger _logger = logger.ForContext<TuyaLocalClient>();
    private const int Port = 6668;
    private const uint Prefix = 21930u;
    private const uint Suffix = 43605u;

    private int _seq;

    public async Task<JObject> GetStatusAsync(TuyaDeviceRecord device, int timeoutMs = 5000)
    {
        if (IsCloud(device))
        {
            return await GetStatusCloudAsync(device);
        }

        if (IsV35(device))
        {
            return await TuyaLocalClientV35.GetStatusAsync(device, timeoutMs, _logger);
        }

        if (IsV34(device))
        {
            return await GetStatusV34Async(device, timeoutMs);
        }

        var payload = new JObject
        {
            ["gwId"] = device.Id,
            ["devId"] = device.Id,
            ["uid"] = device.Id,
            ["t"] = UnixTime().ToString(),
        };

        var response = await SendAsync(device, 10, payload.ToString(Formatting.None), prependHeader: false, timeoutMs);
        if (response is null)
        {
            _logger.Debug("[LocalClient] DP_QUERY(10) rejected - trying CONTROL_NEW(13) as a 'device22'...");
            response = await SendAsync(device, 13, payload.ToString(Formatting.None), prependHeader: false, timeoutMs);
        }

        return (response?["dps"] as JObject) ?? response ?? [];
    }

    public async Task SendCommandAsync(TuyaDeviceRecord device, string code, object value, int timeoutMs = 5000)
    {
        if (IsCloud(device))
        {
            await cloudClient.SendCommandAsync(device.Id, code, value);
            return;
        }

        code = ToLocalDpCode(code);

        if (IsV35(device))
        {
            await TuyaLocalClientV35.SendCommandAsync(device, code, value, timeoutMs, _logger);
            return;
        }

        if (IsV34(device))
        {
            await SendCommandV34Async(device, code, value, timeoutMs);
            return;
        }

        var dps = new JObject { [code] = JToken.FromObject(value) };
        var payload = new JObject
        {
            ["devId"] = device.Id,
            ["uid"] = string.Empty,
            ["t"] = UnixTime().ToString(),
            ["dps"] = dps,
        };

        if (await SendAsync(device, 7, payload.ToString(Formatting.None), prependHeader: true, timeoutMs) is null)
        {
            _logger.Debug("[LocalClient] CONTROL(7) rejected - trying CONTROL_NEW(13) as a 'device22'...");
            await SendAsync(device, 13, payload.ToString(Formatting.None), prependHeader: true, timeoutMs);
        }
    }

    private static bool IsV34(TuyaDeviceRecord device) => device.ProtocolVersion.Trim().StartsWith("3.4");
    private static bool IsV35(TuyaDeviceRecord device) => device.ProtocolVersion.Trim().StartsWith("3.5");
    private static bool IsCloud(TuyaDeviceRecord device) => string.Equals(device.ProtocolVersion.Trim(), "cloud", StringComparison.OrdinalIgnoreCase);

    internal static string ToLocalDpCode(string code) => code.Trim() switch
    {
        "switch_1" => "1",
        "countdown_1" => "9",
        "relay_status" => "38",
        "random_time" => "42",
        "cycle_time" => "43",
        "switch_inching" => "44",
        "switch_type" => "47",
        "remote_add" => "49",
        "remote_list" => "50",
        _ => code.Trim(),
    };

    private async Task<JObject> GetStatusCloudAsync(TuyaDeviceRecord device)
    {
        var statusArray = await cloudClient.GetDeviceStatusAsync(device.Id);
        var result = new JObject();
        if (statusArray is not null)
        {
            foreach (var item in statusArray)
            {
                var code = item["code"]?.ToString();
                if (!string.IsNullOrEmpty(code))
                {
                    result[code] = item["value"];
                }
            }
        }

        return result;
    }

    private static long UnixTime() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private async Task<JObject?> SendAsync(TuyaDeviceRecord device, int command, string jsonPayload, bool prependHeader, int timeoutMs)
    {
        ValidateDevice(device);
        var plain = Encoding.UTF8.GetBytes(jsonPayload);

        var keyBytes = Encoding.UTF8.GetBytes(device.LocalKey!);
        var encrypted = AesEcbEncrypt(plain, keyBytes);
        if (prependHeader)
        {
            encrypted = PrependVersionHeader(encrypted, "3.3");
        }
        var seq = NextSeq();
        var packet = BuildPacketCrc(seq, (uint)command, encrypted);
        _logger.Debug("[LocalClient] (3.3) sending to {Ip}:{Port}, command={Command}, plaintext='{Payload}'", device.IpAddress, Port, command, jsonPayload);

        using var client = new TcpClient();
        if (!await ConnectWithTimeoutAsync(client, device.IpAddress, Port, timeoutMs))
        {
            throw new TuyaApiException($"Could not connect to {device.IpAddress}:{Port} (timeout).");
        }

        using var stream = client.GetStream();
        await stream.WriteAsync(packet);
        var response = await ReadWithTimeoutAsync(stream, timeoutMs);
        if (response is null)
        {
            if (command == 7)
            {
                return [];
            }

            throw new TuyaApiException("The device did not respond (timeout).");
        }

        if (response.Length == 0)
        {
            _logger.Debug("[LocalClient] connection closed with no data - treating as rejected by the device.");
            return null;
        }

        return ParseResponseCrc(response, keyBytes);
    }

    private static byte[] BuildPacketCrc(uint seq, uint command, byte[] encryptedPayload)
    {
        var length = (uint)(encryptedPayload.Length + 8);
        using var ms = new MemoryStream();
        WriteUInt32BE(ms, Prefix);
        WriteUInt32BE(ms, seq);
        WriteUInt32BE(ms, command);
        WriteUInt32BE(ms, length);
        ms.Write(encryptedPayload, 0, encryptedPayload.Length);
        var crc = Crc32.Compute(ms.ToArray());
        WriteUInt32BE(ms, crc);
        WriteUInt32BE(ms, Suffix);
        return ms.ToArray();
    }

    private JObject ParseResponseCrc(byte[] data, byte[] keyBytes)
    {
        if (data.Length < 16)
        {
            return [];
        }

        var declaredLength = ReadUInt32BE(data, 12);
        var headerLen = 16;
        var payloadLen = (int)(declaredLength - 8);
        if (payloadLen < 0 || headerLen + payloadLen > data.Length)
        {
            payloadLen = Math.Max(0, data.Length - headerLen - 8);
        }

        if (payloadLen <= 0)
        {
            return [];
        }

        var payload = new byte[payloadLen];
        Array.Copy(data, headerLen, payload, 0, payloadLen);
        _logger.Debug("[LocalClient] (3.3) declaredLength={Declared}, payloadLen={Payload}", declaredLength, payloadLen);
        var text = TryDecryptToJsonText(payload, keyBytes);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            return JObject.Parse(text);
        }
        catch
        {
            return [];
        }
    }

    private async Task<JObject> GetStatusV34Async(TuyaDeviceRecord device, int timeoutMs)
    {
        ValidateDevice(device);
        using var client = new TcpClient();
        if (!await ConnectWithTimeoutAsync(client, device.IpAddress, Port, timeoutMs))
        {
            throw new TuyaApiException($"Could not connect to {device.IpAddress}:{Port} (timeout).");
        }

        using var stream = client.GetStream();
        var sessionKey = await NegotiateSessionKeyAsync(stream, device.LocalKey!, timeoutMs);
        var payload = new JObject();
        var response = await SendV34CommandOnStreamAsync(stream, sessionKey, 16, payload, prependHeader: false, timeoutMs);

        return (response?["dps"] as JObject) ?? response ?? [];
    }

    private async Task SendCommandV34Async(TuyaDeviceRecord device, string code, object value, int timeoutMs)
    {
        ValidateDevice(device);
        using var client = new TcpClient();
        if (!await ConnectWithTimeoutAsync(client, device.IpAddress, Port, timeoutMs))
        {
            throw new TuyaApiException($"Could not connect to {device.IpAddress}:{Port} (timeout).");
        }

        using var stream = client.GetStream();
        var sessionKey = await NegotiateSessionKeyAsync(stream, device.LocalKey!, timeoutMs);
        var payload = new JObject
        {
            ["protocol"] = 5,
            ["t"] = UnixTime(),
            ["data"] = new JObject { ["dps"] = new JObject { [code] = JToken.FromObject(value) } },
        };

        await SendV34CommandOnStreamAsync(stream, sessionKey, 13, payload, prependHeader: true, timeoutMs);
    }

    private async Task<byte[]> NegotiateSessionKeyAsync(NetworkStream stream, string realLocalKeyStr, int timeoutMs)
    {
        var realKey = Encoding.UTF8.GetBytes(realLocalKeyStr);
        var clientNonce = new byte[16];
        RandomNumberGenerator.Fill(clientNonce);

        var encryptedClientNonce = AesEcbEncrypt(clientNonce, realKey);
        var startPacket = BuildPacketHmac(NextSeq(), 3u, encryptedClientNonce, realKey);
        _logger.Debug("[LocalClient] (3.4) sending SESS_KEY_NEG_START.");
        await stream.WriteAsync(startPacket);

        var respRaw = await ReadWithTimeoutAsync(stream, timeoutMs);
        if (respRaw is null || respRaw.Length == 0)
        {
            throw new TuyaApiException("The device did not respond to session negotiation (SESS_KEY_NEG_RESP) - this is probably not protocol 3.4, or the local key is wrong.");
        }

        var respPayload = ExtractPayloadHmacFrame(respRaw) ?? throw new TuyaApiException("Could not parse the SESS_KEY_NEG_RESP response.");
        var respDecrypted = AesEcbDecryptRawWithPadding(respPayload, realKey);
        if (respDecrypted is null || respDecrypted.Length < 48)
        {
            throw new TuyaApiException("Could not decrypt SESS_KEY_NEG_RESP (check that the local key is correct).");
        }

        var deviceNonce = new byte[16];
        Array.Copy(respDecrypted, 0, deviceNonce, 0, 16);

        byte[] finishHmac;
        using (var h = new HMACSHA256(realKey))
        {
            finishHmac = h.ComputeHash(deviceNonce);
        }

        var encryptedFinishHmac = AesEcbEncrypt(finishHmac, realKey);
        var finishPacket = BuildPacketHmac(NextSeq(), 5u, encryptedFinishHmac, realKey);
        _logger.Debug("[LocalClient] (3.4) sending SESS_KEY_NEG_FINISH.");
        await stream.WriteAsync(finishPacket);

        var xorNonce = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            xorNonce[i] = (byte)(clientNonce[i] ^ deviceNonce[i]);
        }

        var sessionKey = AesEcbEncryptNoPadding(xorNonce, realKey);
        _logger.Debug("[LocalClient] (3.4) session negotiated, session_key established.");
        return sessionKey;
    }

    private async Task<JObject?> SendV34CommandOnStreamAsync(NetworkStream stream, byte[] sessionKey, int command, JObject payload, bool prependHeader, int timeoutMs)
    {
        var plain = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
        if (prependHeader)
        {
            plain = PrependVersionHeader(plain, "3.4");
        }

        var encrypted = AesEcbEncrypt(plain, sessionKey);
        var seq = NextSeq();
        var packet = BuildPacketHmac(seq, (uint)command, encrypted, sessionKey);
        _logger.Debug("[LocalClient] (3.4) sending command={Command}, plaintext='{Payload}'", command, payload.ToString(Formatting.None));
        await stream.WriteAsync(packet);

        var response = await ReadWithTimeoutAsync(stream, timeoutMs);
        if (response is null)
        {
            if (command is 7 or 13)
            {
                return [];
            }

            throw new TuyaApiException("The device did not respond (timeout, 3.4).");
        }

        if (response.Length == 0)
        {
            _logger.Debug("[LocalClient] (3.4) connection closed with no data.");
            return null;
        }

        return ParseResponseHmac(response, sessionKey);
    }

    private static byte[] BuildPacketHmac(uint seq, uint command, byte[] encryptedPayload, byte[] sessionKey)
    {
        var length = (uint)(encryptedPayload.Length + 32 + 4);
        using var ms = new MemoryStream();
        WriteUInt32BE(ms, Prefix);
        WriteUInt32BE(ms, seq);
        WriteUInt32BE(ms, command);
        WriteUInt32BE(ms, length);
        ms.Write(encryptedPayload, 0, encryptedPayload.Length);
        var buffer = ms.ToArray();

        byte[] hmac;
        using (var h = new HMACSHA256(sessionKey))
        {
            hmac = h.ComputeHash(buffer);
        }

        ms.Write(hmac, 0, hmac.Length);
        WriteUInt32BE(ms, Suffix);
        return ms.ToArray();
    }

    private JObject ParseResponseHmac(byte[] data, byte[] sessionKey)
    {
        if (data.Length < 16)
        {
            return [];
        }

        var declaredLength = ReadUInt32BE(data, 12);
        var cipherLen = (int)(declaredLength - 4 - 32 - 4);
        const int headerLen = 20;
        if (cipherLen < 0 || headerLen + cipherLen > data.Length)
        {
            cipherLen = Math.Max(0, data.Length - headerLen - 32 - 4);
        }

        if (cipherLen <= 0)
        {
            return [];
        }

        var cipher = new byte[cipherLen];
        Array.Copy(data, headerLen, cipher, 0, cipherLen);
        _logger.Debug("[LocalClient] (3.4) declaredLength={Declared}, cipherLen={Cipher}", declaredLength, cipherLen);

        var decrypted = AesEcbDecryptRawWithPadding(cipher, sessionKey);
        if (decrypted is null)
        {
            return [];
        }

        var text = SafeUtf8(decrypted);
        if (text is null)
        {
            return [];
        }

        text = StripVersionHeader(text.Trim().TrimEnd('\0'));
        if (!LooksLikeJson(text))
        {
            return [];
        }

        try
        {
            return JObject.Parse(text);
        }
        catch
        {
            return [];
        }
    }

    private static byte[]? ExtractPayloadHmacFrame(byte[] data)
    {
        if (data.Length < 16)
        {
            return null;
        }

        var declaredLength = ReadUInt32BE(data, 12);
        var headerLen = 16;
        var payloadLen = (int)(declaredLength - 4 - 32 - 4);
        if (payloadLen < 0 || headerLen + 4 + payloadLen > data.Length)
        {
            payloadLen = Math.Max(0, data.Length - headerLen - 4 - 32 - 4);
        }

        if (payloadLen <= 0)
        {
            return null;
        }

        var payload = new byte[payloadLen];
        Array.Copy(data, headerLen + 4, payload, 0, payloadLen);
        return payload;
    }

    private static async Task<bool> ConnectWithTimeoutAsync(TcpClient client, string ip, int port, int timeoutMs)
    {
        var connectTask = client.ConnectAsync(ip, port);
        if (await Task.WhenAny(connectTask, Task.Delay(timeoutMs)) != connectTask)
        {
            return false;
        }

        await connectTask;
        return true;
    }

    private static async Task<byte[]?> ReadWithTimeoutAsync(NetworkStream stream, int timeoutMs)
    {
        var header = new byte[16];
        if (!await ReadExactlyWithTimeoutAsync(stream, header, timeoutMs))
        {
            return null;
        }

        var declaredLength = ReadUInt32BE(header, 12);
        var remainingLength = (long)declaredLength;
        if (remainingLength < 8 || remainingLength > 1024 * 1024)
        {
            return header;
        }

        var response = new byte[header.Length + (int)remainingLength];
        Array.Copy(header, response, header.Length);
        if (!await ReadExactlyWithTimeoutAsync(stream, response.AsMemory(header.Length), timeoutMs))
        {
            return null;
        }

        return response;
    }

    private static async Task<bool> ReadExactlyWithTimeoutAsync(NetworkStream stream, Memory<byte> buffer, int timeoutMs)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var readTask = stream.ReadAsync(buffer[offset..]).AsTask();
            if (await Task.WhenAny(readTask, Task.Delay(timeoutMs)) != readTask)
            {
                return false;
            }

            var read = readTask.Result;
            if (read <= 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private static void ValidateDevice(TuyaDeviceRecord device)
    {
        if (string.IsNullOrWhiteSpace(device.IpAddress))
        {
            throw new TuyaApiException("This device has no IP address set (configure it via the device config flow).");
        }

        if (string.IsNullOrWhiteSpace(device.LocalKey))
        {
            throw new TuyaApiException("This device has no local key saved - sync devices from the cloud again.");
        }
    }

    private uint NextSeq() => (uint)Interlocked.Increment(ref _seq);

    private static byte[] PrependVersionHeader(byte[] plain, string version)
    {
        var header = new byte[15];
        Encoding.ASCII.GetBytes(version).CopyTo(header, 0);
        var result = new byte[header.Length + plain.Length];
        header.CopyTo(result, 0);
        plain.CopyTo(result, header.Length);
        return result;
    }

    private static string? TryDecryptToJsonText(byte[] payload, byte[] keyBytes)
    {
        foreach (var skip in new[] { 0, 4 })
        {
            if (payload.Length - skip <= 0)
            {
                continue;
            }

            var slice = new byte[payload.Length - skip];
            Array.Copy(payload, skip, slice, 0, slice.Length);
            var decrypted = AesEcbDecryptRawWithPadding(slice, keyBytes);
            if (decrypted is null)
            {
                continue;
            }

            var text = SafeUtf8(decrypted);
            if (text is null)
            {
                continue;
            }

            var stripped = StripVersionHeader(text);
            if (LooksLikeJson(stripped))
            {
                return stripped;
            }
        }

        return null;
    }

    private static string StripVersionHeader(string s)
    {
        if (s.Length >= 15 && (s.StartsWith("3.3") || s.StartsWith("3.1") || s.StartsWith("3.4")))
        {
            return s[15..].TrimEnd('\0').TrimStart('\0');
        }

        return s;
    }

    private static bool LooksLikeJson(string s)
    {
        var text = s.Trim().TrimEnd('\0');
        return text.StartsWith('{') && text.EndsWith('}');
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

    private static byte[] AesEcbEncrypt(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        using var enc = aes.CreateEncryptor();
        return enc.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] AesEcbEncryptNoPadding(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        using var enc = aes.CreateEncryptor();
        return enc.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[]? AesEcbDecryptRawWithPadding(byte[] data, byte[] key)
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
            return StripPkcs7IfValid(raw);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] StripPkcs7IfValid(byte[] data)
    {
        if (data.Length == 0)
        {
            return data;
        }

        int pad = data[^1];
        if (pad > 0 && pad <= 16 && pad <= data.Length)
        {
            var valid = true;
            for (var i = data.Length - pad; i < data.Length; i++)
            {
                if (data[i] != pad)
                {
                    valid = false;
                    break;
                }
            }

            if (valid)
            {
                var trimmed = new byte[data.Length - pad];
                Array.Copy(data, trimmed, trimmed.Length);
                return trimmed;
            }
        }

        return data;
    }

    private static void WriteUInt32BE(Stream s, uint value)
    {
        s.WriteByte((byte)(value >> 24));
        s.WriteByte((byte)(value >> 16));
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }

    private static uint ReadUInt32BE(byte[] data, int offset) =>
        (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
}

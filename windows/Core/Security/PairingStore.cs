using System.Security.Cryptography;
using System.Text.Json;
using IPhoneWebcam.Core.Diagnostics;

namespace IPhoneWebcam.Core.Security;

public interface IPairingStore
{
    /// <summary>Stable random identifier for this PC (not a hardware ID).</summary>
    string PcId { get; }

    byte[]? GetKey(string deviceId);

    void SaveKey(string deviceId, byte[] key);

    void RemoveKey(string deviceId);
}

public sealed class InMemoryPairingStore(string? pcId = null) : IPairingStore
{
    private readonly Dictionary<string, byte[]> _keys = [];

    public string PcId { get; } = pcId ?? Guid.NewGuid().ToString();

    public byte[]? GetKey(string deviceId)
    {
        lock (_keys)
            return _keys.GetValueOrDefault(deviceId);
    }

    public void SaveKey(string deviceId, byte[] key)
    {
        lock (_keys)
            _keys[deviceId] = key;
    }

    public void RemoveKey(string deviceId)
    {
        lock (_keys)
            _keys.Remove(deviceId);
    }
}

/// <summary>Per-user store in %LOCALAPPDATA%\iPhoneWebcam\pairings.json; keys protected with DPAPI (CurrentUser).</summary>
public sealed class DpapiPairingStore : IPairingStore
{
    private sealed record FileModel(string PcId, Dictionary<string, string> Keys);

    private static readonly byte[] Entropy = "iPhoneWebcam.pairing.v1"u8.ToArray();
    private readonly string _path;
    private readonly object _gate = new();
    private readonly FileModel _model;

    public DpapiPairingStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iPhoneWebcam");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "pairings.json");
        _model = Load(_path) ?? new FileModel(Guid.NewGuid().ToString(), []);
        Persist();
    }

    public string PcId => _model.PcId;

    public byte[]? GetKey(string deviceId)
    {
        lock (_gate)
        {
            if (!_model.Keys.TryGetValue(deviceId, out var protectedKey))
                return null;
            try
            {
                return ProtectedData.Unprotect(Convert.FromBase64String(protectedKey), Entropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                Log.Warn("Stored pairing key could not be decrypted; re-pairing required");
                return null;
            }
        }
    }

    public void SaveKey(string deviceId, byte[] key)
    {
        lock (_gate)
        {
            _model.Keys[deviceId] = Convert.ToBase64String(ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
            Persist();
        }
    }

    public void RemoveKey(string deviceId)
    {
        lock (_gate)
        {
            if (_model.Keys.Remove(deviceId))
                Persist();
        }
    }

    private static FileModel? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Warn($"Pairing store unreadable, starting fresh: {ex.Message}");
            return null;
        }
    }

    private void Persist()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_model));
        File.Move(tmp, _path, overwrite: true);
    }
}

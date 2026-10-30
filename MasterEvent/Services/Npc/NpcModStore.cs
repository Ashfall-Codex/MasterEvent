using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace MasterEvent.Services.Npc;


public sealed class NpcModStore
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    private readonly string filesDirectory;
    private readonly string manifestsDirectory;
    private readonly ConcurrentDictionary<string, string> index = new(StringComparer.Ordinal);

    public NpcModStore(string pluginConfigDir)
    {
        var root = Path.Combine(pluginConfigDir, "mods");
        filesDirectory = Path.Combine(root, "files");
        manifestsDirectory = Path.Combine(root, "manifests");
        Directory.CreateDirectory(filesDirectory);
        Directory.CreateDirectory(manifestsDirectory);
        ScanAndPrune();
    }

    public string FilesDirectory => filesDirectory;

    public bool Contains(string hash) => TryGetPath(hash, out _);

    public bool TryGetPath(string hash, out string path)
    {
        path = string.Empty;
        if (!index.TryGetValue(hash, out var known)) return false;
        if (!File.Exists(known))
        {
            index.TryRemove(hash, out _);
            return false;
        }

        path = known;
        return true;
    }

    public void Touch(string hash)
    {
        if (!TryGetPath(hash, out var path)) return;
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
        catch { /* sans conséquence : le fichier sera simplement purgé plus tôt */ }
    }


    public bool TryStore(string hash, string extension, Stream source, long length, byte[] buffer)
    {
        var finalPath = Path.Combine(filesDirectory, hash + SafeExtension(extension));
        var tmpPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".part";

        try
        {
            var actual = CopyHashing(source, tmpPath, length, buffer);
            if (!string.Equals(actual, hash, StringComparison.Ordinal))
            {
                File.Delete(tmpPath);
                return false;
            }

            File.Move(tmpPath, finalPath, overwrite: true);
            index[hash] = finalPath;
            return true;
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { /* déjà absent */ }
            throw;
        }
    }

    public string StoreComputingHash(string extension, Stream source, long length, byte[] buffer)
    {
        var tmpPath = NewTempPath();

        try
        {
            var hash = CopyHashing(source, tmpPath, length, buffer);
            if (Contains(hash))
            {
                File.Delete(tmpPath);
                return hash;
            }

            var finalPath = Path.Combine(filesDirectory, hash + SafeExtension(extension));
            File.Move(tmpPath, finalPath, overwrite: true);
            index[hash] = finalPath;
            return hash;
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { /* déjà absent */ }
            throw;
        }
    }

    private static string CopyHashing(Stream source, string destination, long length, byte[] buffer)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var dest = File.Create(destination);

        var remaining = length;
        while (remaining > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0) throw new EndOfStreamException("Flux tronqué.");
            sha1.AppendData(buffer, 0, read);
            dest.Write(buffer, 0, read);
            remaining -= read;
        }

        return Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
    }

    public bool TryAdopt(string hash, string extension, string tempFile)
    {
        try
        {
            using var stream = File.OpenRead(tempFile);
            var actual = Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actual, hash, StringComparison.Ordinal)) return false;
        }
        catch
        {
            return false;
        }

        var finalPath = Path.Combine(filesDirectory, hash + SafeExtension(extension));
        File.Move(tempFile, finalPath, overwrite: true);
        index[hash] = finalPath;
        return true;
    }

    public string NewTempPath() => Path.Combine(filesDirectory, Guid.NewGuid().ToString("N") + ".part");

    public void SaveManifest(string id, byte[] bytes)
    {
        if (!IsValidId(id)) return;
        File.WriteAllBytes(Path.Combine(manifestsDirectory, id + ".json"), bytes);
    }

    public byte[]? LoadManifest(string id)
    {
        if (!IsValidId(id)) return null;
        var path = Path.Combine(manifestsDirectory, id + ".json");
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch { return null; }
    }

    private static bool IsValidId(string id) => Models.NpcModManifest.IsValidHash(id);

    private static string SafeExtension(string extension)
    {
        if (extension.Length is < 2 or > 8 || extension[0] != '.') return ".bin";
        return extension.Skip(1).All(char.IsAsciiLetterOrDigit) ? extension.ToLowerInvariant() : ".bin";
    }

    private void ScanAndPrune()
    {
        var limit = DateTime.UtcNow - MaxAge;
        try
        {
            foreach (var path in Directory.EnumerateFiles(filesDirectory))
            {
                var name = Path.GetFileName(path);
                if (name.EndsWith(".part", StringComparison.Ordinal) || File.GetLastWriteTimeUtc(path) < limit)
                {
                    try { File.Delete(path); } catch { /* verrouillé : prochaine fois */ }
                    continue;
                }

                var hash = Path.GetFileNameWithoutExtension(name);
                if (Models.NpcModManifest.IsValidHash(hash)) index[hash] = path;
            }

            foreach (var path in Directory.EnumerateFiles(manifestsDirectory))
            {
                if (File.GetLastWriteTimeUtc(path) < limit)
                {
                    try { File.Delete(path); } catch { /* idem */ }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[NpcMods] Analyse du cache impossible : {ex.Message}");
        }
    }
}

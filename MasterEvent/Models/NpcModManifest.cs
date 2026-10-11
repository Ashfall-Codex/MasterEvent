using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MasterEvent.Models;

public sealed class NpcModManifest
{
    public const int CurrentVersion = 1;
    public const int MaxFiles = 4000;
    public const int MaxPathsPerEntry = 64;
    public const int MaxGlamourerLength = 512 * 1024;
    public const int MaxManipulationLength = 2 * 1024 * 1024;
    public const long MaxFileSize = 200L * 1024 * 1024;
    public const long MaxTotalSize = 1024L * 1024 * 1024;
    public const int MaxManifestBytes = 8 * 1024 * 1024;

    [JsonPropertyName("v")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("glam")]
    public string Glamourer { get; set; } = string.Empty;

    [JsonPropertyName("meta")]
    public string Manipulation { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public List<ModFile> Files { get; set; } = new();

    [JsonPropertyName("swaps")]
    public List<ModSwap> Swaps { get; set; } = new();

    public sealed class ModFile
    {
        [JsonPropertyName("paths")]
        public List<string> Paths { get; set; } = new();

        [JsonPropertyName("hash")]
        public string Hash { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }

    public sealed class ModSwap
    {
        [JsonPropertyName("paths")]
        public List<string> Paths { get; set; } = new();

        [JsonPropertyName("target")]
        public string Target { get; set; } = string.Empty;
    }

    [JsonIgnore]
    public long TotalFileSize => Files.Sum(f => f.Size);

    public bool IsEmpty => string.IsNullOrEmpty(Glamourer) && string.IsNullOrEmpty(Manipulation)
                           && Files.Count == 0 && Swaps.Count == 0;


    private static readonly Regex GamePathPattern =
        new(@"^[A-Za-z0-9_\-./]{1,260}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HashPattern =
        new(@"^[0-9a-f]{40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mdl", ".tex", ".mtrl", ".tmb", ".pap", ".avfx", ".atex", ".sklb",
        ".eid", ".phyb", ".pbd", ".scd", ".skp", ".shpk", ".kdb",
    };

    public static bool IsValidHash(string? hash) => hash != null && HashPattern.IsMatch(hash);

    public static bool IsValidGamePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || !GamePathPattern.IsMatch(path)) return false;
        if (path.StartsWith('/') || path.Contains("..", StringComparison.Ordinal)
            || path.Contains("//", StringComparison.Ordinal)) return false;

        var dot = path.LastIndexOf('.');
        return dot >= 0 && AllowedExtensions.Contains(path[dot..]);
    }

    public static string ExtensionFor(ModFile file)
    {
        var first = file.Paths.FirstOrDefault();
        var dot = first?.LastIndexOf('.') ?? -1;
        return dot >= 0 ? first![dot..].ToLowerInvariant() : ".bin";
    }

    public bool Sanitize(out string? error)
    {
        error = null;
        if (Version != CurrentVersion)
        {
            error = $"Version de manifeste non supportée : {Version}.";
            return false;
        }

        Glamourer ??= string.Empty;
        Manipulation ??= string.Empty;
        if (Glamourer.Length > MaxGlamourerLength || Manipulation.Length > MaxManipulationLength)
        {
            error = "État Glamourer ou manipulations démesurés.";
            return false;
        }

        if (Files.Count > MaxFiles || Swaps.Count > MaxFiles)
        {
            error = "Trop de fichiers dans le manifeste.";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<ModFile>();
        foreach (var file in Files)
        {
            file.Hash = (file.Hash ?? string.Empty).ToLowerInvariant();
            if (!IsValidHash(file.Hash) || file.Size is <= 0 or > MaxFileSize) continue;
            if (file.Paths == null) continue;

            file.Paths = file.Paths.Take(MaxPathsPerEntry).Where(IsValidGamePath).Distinct(StringComparer.Ordinal).ToList();
            if (file.Paths.Count == 0) continue;
            if (!seen.Add(file.Hash)) continue;
            files.Add(file);
        }
        Files = files;

        var swaps = new List<ModSwap>();
        foreach (var swap in Swaps)
        {
            if (swap.Paths == null || !IsValidGamePath(swap.Target)) continue;
            swap.Paths = swap.Paths.Take(MaxPathsPerEntry).Where(IsValidGamePath).Distinct(StringComparer.Ordinal).ToList();
            if (swap.Paths.Count > 0) swaps.Add(swap);
        }
        Swaps = swaps;

        if (TotalFileSize > MaxTotalSize)
        {
            error = "Taille cumulée des fichiers au-delà de la limite.";
            return false;
        }

        return true;
    }


    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this);

    public static string ComputeId(byte[] manifestBytes)
        => Convert.ToHexString(SHA1.HashData(manifestBytes)).ToLowerInvariant();

    public static NpcModManifest? FromBytes(byte[] bytes, out string? error)
    {
        error = null;
        try
        {
            var manifest = JsonSerializer.Deserialize<NpcModManifest>(Encoding.UTF8.GetString(bytes));
            if (manifest == null)
            {
                error = "Manifeste vide.";
                return null;
            }
            return manifest.Sanitize(out error) ? manifest : null;
        }
        catch (Exception ex)
        {
            error = $"Manifeste illisible : {ex.Message}";
            return null;
        }
    }
}

public sealed class NpcModRef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("files")]
    public int FileCount { get; set; }
}

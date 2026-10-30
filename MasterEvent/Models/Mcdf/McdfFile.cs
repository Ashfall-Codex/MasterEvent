using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using K4os.Compression.LZ4.Legacy;
using MasterEvent.Services.Npc;

namespace MasterEvent.Models.Mcdf;

public sealed class McdfFile
{
    private const byte SupportedVersion = 1;

    [JsonPropertyName("Description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("GlamourerData")]
    public string GlamourerData { get; set; } = string.Empty;

    [JsonPropertyName("CustomizePlusData")]
    public string CustomizePlusData { get; set; } = string.Empty;

    [JsonPropertyName("ManipulationData")]
    public string ManipulationData { get; set; } = string.Empty;

    [JsonPropertyName("Files")]
    public List<McdfFileEntry> Files { get; set; } = new();

    [JsonPropertyName("FileSwaps")]
    public List<McdfSwapEntry> FileSwaps { get; set; } = new();

    public sealed class McdfFileEntry
    {
        public List<string> GamePaths { get; set; } = new();
        public int Length { get; set; }
        public string Hash { get; set; } = string.Empty;
    }

    public sealed class McdfSwapEntry
    {
        public List<string> GamePaths { get; set; } = new();
        public string FileSwapPath { get; set; } = string.Empty;
    }

    // Résultat d'une extraction : le manifeste prêt à publier et ce qui n'a pas pu être repris.
    public sealed record ExtractResult(NpcModManifest Manifest, int Rejected, string? FirstRejection);

    // Lit l'en-tête seul (rapide) : sert à valider le fichier avant de toucher au disque.
    public static McdfFile ReadHeader(string path)
    {
        using var fs = File.OpenRead(path);
        using var lz4 = new LZ4Stream(fs, LZ4StreamMode.Decompress, LZ4StreamFlags.HighCompression);
        using var reader = new BinaryReader(lz4, Encoding.UTF8, leaveOpen: true);
        return ReadHeader(reader);
    }

    private static McdfFile ReadHeader(BinaryReader reader)
    {
        var magic = new string(reader.ReadChars(4));
        if (!string.Equals(magic, "MCDF", StringComparison.Ordinal))
            throw new InvalidDataException("Ce fichier n'est pas un MCDF.");

        var version = reader.ReadByte();
        if (version != SupportedVersion)
            throw new InvalidDataException($"Version MCDF non supportée : {version}.");

        var length = reader.ReadInt32();
        if (length is <= 0 or > 16 * 1024 * 1024)
            throw new InvalidDataException("En-tête MCDF de taille invalide.");

        var json = Encoding.UTF8.GetString(reader.ReadBytes(length));
        return JsonSerializer.Deserialize<McdfFile>(json)
               ?? throw new InvalidDataException("En-tête MCDF illisible.");
    }

    // Extrait les fichiers dans le cache local (vérifiés par SHA-1) et construit le manifeste.
    public static ExtractResult Extract(string path, NpcModStore store, Action<long, long>? progress = null)
    {
        using var fs = File.OpenRead(path);
        using var lz4 = new LZ4Stream(fs, LZ4StreamMode.Decompress, LZ4StreamFlags.HighCompression);
        using var reader = new BinaryReader(lz4, Encoding.UTF8, leaveOpen: true);

        var header = ReadHeader(reader);
        var manifest = new NpcModManifest
        {
            Glamourer = header.GlamourerData ?? string.Empty,
            Manipulation = header.ManipulationData ?? string.Empty,
            CustomizePlus = header.CustomizePlusData ?? string.Empty,
        };

        foreach (var swap in header.FileSwaps ?? [])
        {
            manifest.Swaps.Add(new NpcModManifest.ModSwap
            {
                Paths = swap.GamePaths?.ToList() ?? [],
                Target = swap.FileSwapPath ?? string.Empty,
            });
        }

        var entries = header.Files ?? [];
        var total = entries.Sum(e => (long)Math.Max(e.Length, 0));
        long done = 0;
        var rejected = 0;
        string? firstRejection = null;
        var buffer = new byte[128 * 1024];

        foreach (var entry in entries)
        {
            // Les fichiers se suivent dans le flux : même un fichier écarté doit être consommé.
            var length = Math.Max(entry.Length, 0);
            var hash = (entry.Hash ?? string.Empty).ToLowerInvariant();
            var isBlake3 = NpcModManifest.IsBlake3Hash(hash);
            var paths = (entry.GamePaths ?? []).Where(NpcModManifest.IsValidGamePath).Distinct().ToList();

            string? rejection = null;
            if (length <= 0 || length > NpcModManifest.MaxFileSize)
                rejection = $"taille {length}";
            else if (!isBlake3 && !NpcModManifest.IsValidHash(hash))
                rejection = $"hash « {hash} »";
            else if (paths.Count == 0)
                rejection = $"chemin « {entry.GamePaths?.FirstOrDefault()} »";

            if (rejection == null)
            {
                var file = new NpcModManifest.ModFile { Paths = paths, Hash = hash, Size = length };
                var ext = NpcModManifest.ExtensionFor(file);

                if (isBlake3)
                {
                    file.Hash = store.StoreComputingHash(ext, reader.BaseStream, length, buffer);
                }
                else if (store.Contains(hash))
                {
                    Skip(reader, length, buffer);
                }
                else if (!store.TryStore(hash, ext, reader.BaseStream, length, buffer))
                {
                    rejection = $"contenu différent du hash {hash}";
                }

                if (rejection == null) manifest.Files.Add(file);
            }
            else
            {
                Skip(reader, length, buffer);
            }

            if (rejection != null)
            {
                rejected++;
                firstRejection ??= rejection;
            }

            done += length;
            progress?.Invoke(done, total);
        }

        if (!manifest.Sanitize(out var error))
            throw new InvalidDataException(error ?? "Manifeste invalide.");

        return new ExtractResult(manifest, rejected, firstRejection);
    }

    private static void Skip(BinaryReader reader, int length, byte[] buffer)
    {
        var remaining = length;
        while (remaining > 0)
        {
            var read = reader.BaseStream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read <= 0) throw new EndOfStreamException("MCDF tronqué.");
            remaining -= read;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using K4os.Compression.LZ4.Legacy;

namespace MasterEvent.Models.Mcdf;


public sealed record MareCharaFileHeader(byte Version, MareCharaFileData CharaFileData)
{
    public byte Version { get; set; } = Version;
    public MareCharaFileData CharaFileData { get; set; } = CharaFileData;
    public static MareCharaFileHeader? FromBinaryReader(BinaryReader reader)
    {
        var magic = new string(reader.ReadChars(4));
        if (!string.Equals(magic, "MCDF", StringComparison.Ordinal))
            throw new InvalidDataException($"Pas un fichier MCDF (magic = '{magic}').");

        var version = reader.ReadByte();
        if (version != 1)
            throw new InvalidDataException($"Version MCDF non supportée : {version}.");

        var dataLength = reader.ReadInt32();
        var dataBytes = reader.ReadBytes(dataLength);
        var data = MareCharaFileData.FromByteArray(dataBytes);
        return new MareCharaFileHeader(version, data);
    }

    public static MareCharaFileHeader LoadFromFile(string path)
    {
        using var fs = File.OpenRead(path);
        using var lz4 = new LZ4Stream(fs, LZ4StreamMode.Decompress, LZ4StreamFlags.HighCompression);
        using var reader = new BinaryReader(lz4);
        var header = FromBinaryReader(reader);
        if (header == null)
            throw new InvalidDataException("Le header MCDF n'a pas pu être lu.");
        return header;
    }

    public Dictionary<string, string> ExtractFilesTo(string sourceMcdfPath, string extractDir, List<string> extractedFilePaths)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var swap in CharaFileData.FileSwaps)
            foreach (var gamePath in swap.GamePaths)
                map[gamePath] = swap.FileSwapPath;

        if (CharaFileData.Files.Count == 0)
            return map;

        using var fs = File.OpenRead(sourceMcdfPath);
        using var lz4 = new LZ4Stream(fs, LZ4StreamMode.Decompress, LZ4StreamFlags.HighCompression);
        using var reader = new BinaryReader(lz4);

        AdvanceReaderPastHeader(reader);

        var counter = 0;
        foreach (var fileData in CharaFileData.Files)
        {
            counter++;
            var fileName = Path.Combine(extractDir, $"mcdf_{Guid.NewGuid():N}_{counter}.tmp");
            extractedFilePaths.Add(fileName);
            var bytes = reader.ReadBytes(fileData.Length);
            File.WriteAllBytes(fileName, bytes);
            foreach (var gamePath in fileData.GamePaths)
                map[gamePath] = fileName;
        }

        return map;
    }

    private static void AdvanceReaderPastHeader(BinaryReader reader)
    {
        reader.ReadChars(4); // magic
        var version = reader.ReadByte();
        if (version != 1)
            throw new InvalidDataException($"Version MCDF non supportée : {version}.");
        var dataLength = reader.ReadInt32();
        _ = reader.ReadBytes(dataLength);
    }
}

public sealed record MareCharaFileData
{
    [JsonPropertyName("Description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("GlamourerData")]
    public string GlamourerData { get; set; } = string.Empty;

    [JsonPropertyName("CustomizePlusData")]
    public string CustomizePlusData { get; set; } = string.Empty;

    [JsonPropertyName("ManipulationData")]
    public string ManipulationData { get; set; } = string.Empty;

    [JsonPropertyName("Files")]
    public List<FileData> Files { get; set; } = new();

    [JsonPropertyName("FileSwaps")]
    public List<FileSwap> FileSwaps { get; set; } = new();

    public static MareCharaFileData FromByteArray(byte[] data)
    {
        var json = Encoding.UTF8.GetString(data);
        return JsonSerializer.Deserialize<MareCharaFileData>(json)
            ?? throw new InvalidDataException("JSON MareCharaFileData invalide.");
    }

    public sealed record FileSwap(IEnumerable<string> GamePaths, string FileSwapPath);

    public sealed record FileData(IEnumerable<string> GamePaths, int Length, string Hash);
}

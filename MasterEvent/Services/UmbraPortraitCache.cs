using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Textures.TextureWraps;

namespace MasterEvent.Services;

public sealed class UmbraPortraitCache : IDisposable
{
    private static readonly TimeSpan UnusedLifetime = TimeSpan.FromMinutes(5);

    private readonly UmbraProfileIpc profiles;
    private readonly Dictionary<uint, Entry> entries = new();

    public UmbraPortraitCache(UmbraProfileIpc profiles) => this.profiles = profiles;

    private sealed class Entry
    {
        public IDalamudTextureWrap? Texture;
        public Task<IDalamudTextureWrap>? Loading;
        public DateTime LastUsed = DateTime.UtcNow;
        public bool Requested;
    }

    public IDalamudTextureWrap? Get(uint objectId)
    {
        if (objectId == 0) return null;

        if (!entries.TryGetValue(objectId, out var entry))
        {
            entry = new Entry();
            entries[objectId] = entry;
        }

        entry.LastUsed = DateTime.UtcNow;

        if (!entry.Requested)
        {
            entry.Requested = true;
            var bytes = profiles.GetPortrait(objectId);
            if (bytes is { Length: > 0 })
                entry.Loading = Plugin.TextureProvider.CreateFromImageAsync(bytes);
        }

        if (entry.Loading is { IsCompletedSuccessfully: true })
        {
            entry.Texture = entry.Loading.Result;
            entry.Loading = null;
        }

        return entry.Texture;
    }

    public static uint ResolveObjectId(string playerName)
    {
        if (string.IsNullOrEmpty(playerName)) return 0;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is IPlayerCharacter pc
                && string.Equals(pc.Name.TextValue, playerName, StringComparison.Ordinal))
                return pc.EntityId;
        }

        return 0;
    }

    public static (Vector2 Uv0, Vector2 Uv1) CoverUv(float textureWidth, float textureHeight,
        float targetWidth, float targetHeight)
    {
        if (textureWidth <= 0f || textureHeight <= 0f || targetWidth <= 0f || targetHeight <= 0f)
            return (Vector2.Zero, Vector2.One);

        var textureRatio = textureWidth / textureHeight;
        var targetRatio = targetWidth / targetHeight;

        if (Math.Abs(textureRatio - targetRatio) < 0.001f)
            return (Vector2.Zero, Vector2.One);

        if (textureRatio > targetRatio)
        {
            var margin = (1f - targetRatio / textureRatio) / 2f;
            return (new Vector2(margin, 0f), new Vector2(1f - margin, 1f));
        }

        var marginY = (1f - textureRatio / targetRatio) / 2f;
        return (new Vector2(0f, marginY), new Vector2(1f, 1f - marginY));
    }

    public void PruneUnused()
    {
        if (entries.Count == 0) return;

        var cutoff = DateTime.UtcNow - UnusedLifetime;
        foreach (var (id, entry) in entries.Where(e => e.Value.LastUsed < cutoff).ToList())
        {
            entry.Texture?.Dispose();
            entries.Remove(id);
        }
    }

    public void Dispose()
    {
        foreach (var entry in entries.Values)
            entry.Texture?.Dispose();
        entries.Clear();
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Models.Mcdf;

namespace MasterEvent.Services.Npc;

public sealed class NpcModService : IDisposable
{
    public const long MaxRemoteModBytes = 512L * 1024 * 1024;

    private static readonly TimeSpan CollectionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DrawTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan VanillaFallbackDelay = TimeSpan.FromSeconds(25);

    private readonly Configuration configuration;
    private readonly NpcManager npcManager;
    private readonly NpcModStore store;
    private readonly NpcModTransfer transfer;
    private readonly API.Glamourer glamourer;
    private readonly API.Penumbra penumbra;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> running = new();
    private readonly CancellationTokenSource lifetime = new();

    public NpcModService(Configuration configuration, NpcManager npcManager, NpcModStore store,
        NpcModTransfer transfer, API.Glamourer glamourer, API.Penumbra penumbra, IFramework framework, IPluginLog log)
    {
        this.configuration = configuration;
        this.npcManager = npcManager;
        this.store = store;
        this.transfer = transfer;
        this.glamourer = glamourer;
        this.penumbra = penumbra;
        this.framework = framework;
        this.log = log;
    }

    public bool PenumbraAvailable => penumbra.Available;
    public bool GlamourerAvailable => glamourer.Available;
    public bool CanPublish => transfer.CanPublish;
    public sealed record ImportResult(NpcModManifest? Manifest, byte[]? Bytes, string? Id, int Rejected, string? Error,
        bool HasWeapon = false, NpcAppearance? Appearance = null, bool IsModded = false);

    public Task<ImportResult> ImportMcdfAsync(string path, Action<long, long>? progress = null)
    {
        return Task.Run(() =>
        {
            try
            {
                var extracted = McdfFile.Extract(path, store, progress);
                if (extracted.Manifest.IsEmpty)
                    return new ImportResult(null, null, null, extracted.Rejected, Loc.Get("Npc.ModsEmpty"));

                var bytes = extracted.Manifest.ToBytes();
                var id = NpcModManifest.ComputeId(bytes);
                store.SaveManifest(id, bytes);
                return new ImportResult(extracted.Manifest, bytes, id, extracted.Rejected, null);
            }
            catch (Exception ex)
            {
                log.Warning($"[NpcMods] Import MCDF impossible : {ex.Message}");
                return new ImportResult(null, null, null, 0, ex.Message);
            }
        });
    }


    public IReadOnlyList<(Guid Id, string Name)> ListDesigns() => glamourer.ListDesigns();
    public async Task<ImportResult> CaptureDesignAsync(Guid designId, Action<string>? status = null)
    {
        if (!glamourer.Available) return Failure(Loc.Get("Npc.ModsNeedGlamourer"));
        if (!penumbra.Available) return Failure(Loc.Get("Npc.ModsNeedPenumbra"));

        PlayerLook? look;
        try
        {
            var playerIndex = await OnFramework(() => (int?)Plugin.ObjectTable.LocalPlayer?.ObjectIndex)
                .ConfigureAwait(false);
            if (playerIndex is not { } index) return Failure(Loc.Get("Npc.DesignNoLocalPlayer"));

            var saved = await OnFramework(() => glamourer.GetState(index)).ConfigureAwait(false);
            if (string.IsNullOrEmpty(saved)) return Failure(Loc.Get("Npc.DesignNoState"));

            status?.Invoke(Loc.Get("Npc.DesignStepApply"));
            if (!await OnFramework(() => glamourer.TryApplyDesign(designId, index)).ConfigureAwait(false))
                return Failure(Loc.Get("Npc.DesignApplyRefused"));

            try
            {
                look = await ReadPlayerLookAsync(index, status).ConfigureAwait(false);
            }
            finally
            {
                await OnFramework(() =>
                {
                    glamourer.TryApplyState(saved, (ushort)index, out _);
                    penumbra.Redraw((ushort)index);
                }).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[NpcMods] Capture du design impossible : {ex.Message}");
            return Failure(ex.Message);
        }

        if (look.Resolved == null) return Failure(Loc.Get("Npc.DesignNoResources"));
        status?.Invoke(Loc.Get("Npc.DesignStepStore"));
        var built = await Task.Run(() => BuildFromResolved(look.State, look.Meta, look.Resolved)).ConfigureAwait(false);
        return built with { HasWeapon = look.HasWeapon, Appearance = look.Appearance, IsModded = true };
    }

    public async Task<ImportResult> CaptureSelfAsync(Action<string>? status = null)
    {
        if (!glamourer.Available || !penumbra.Available) return new ImportResult(null, null, null, 0, null);

        try
        {
            var playerIndex = await OnFramework(() => (int?)Plugin.ObjectTable.LocalPlayer?.ObjectIndex)
                .ConfigureAwait(false);
            if (playerIndex is not { } index) return new ImportResult(null, null, null, 0, null);

            var look = await ReadPlayerLookAsync(index, status).ConfigureAwait(false);
            if (look.Resolved == null) return new ImportResult(null, null, null, 0, null);

            var built = await Task.Run(() => BuildFromResolved(look.State, look.Meta, look.Resolved))
                .ConfigureAwait(false);
            var modded = built.Manifest is { } m && (m.Files.Count > 0 || m.Swaps.Count > 0);

            log.Info($"[NpcMods] Détection sur votre personnage : {(modded ? "moddé" : "non moddé")}"
                     + (built.Manifest is { } mm ? $" ({mm.Files.Count} fichier(s), {mm.Swaps.Count} swap(s))" : string.Empty));

            return built with { HasWeapon = look.HasWeapon, Appearance = look.Appearance, IsModded = modded };
        }
        catch (Exception ex)
        {
            log.Warning($"[NpcMods] Détection des mods impossible : {ex.Message}");
            return new ImportResult(null, null, null, 0, null);
        }
    }

    private sealed record PlayerLook(string State, string Meta, Dictionary<string, HashSet<string>>? Resolved,
        bool HasWeapon, NpcAppearance? Appearance);


    private async Task<PlayerLook> ReadPlayerLookAsync(int index, Action<string>? status)
    {
        var weaponState = await OnFramework(() =>
        {
            NpcInstance.TryReadLocalWeaponState(out var drawn, out var has);
            return (Drawn: drawn, Has: has);
        }).ConfigureAwait(false);

        var forced = weaponState.Has && !weaponState.Drawn;
        try
        {
            if (forced)
                await OnFramework(() => NpcInstance.SetLocalWeaponDrawn(true)).ConfigureAwait(false);

            status?.Invoke(Loc.Get("Npc.DesignStepCapture"));
            await Task.Delay(forced ? 2000 : 1500).ConfigureAwait(false);

            Dictionary<string, HashSet<string>>? resolved = null;
            var lastCount = -1;
            var stable = 0;
            for (var waited = 0; waited < 8000 && stable < 2; waited += 400)
            {
                resolved = await OnFramework(() => penumbra.GetResourcePaths((ushort)index)).ConfigureAwait(false);
                var count = resolved?.Count ?? 0;
                stable = count == lastCount && count > 0 ? stable + 1 : 0;
                lastCount = count;
                await Task.Delay(400).ConfigureAwait(false);
            }

            var state = await OnFramework(() => glamourer.GetState(index)).ConfigureAwait(false);
            var meta = await OnFramework(() => penumbra.GetPlayerMetaManipulations()).ConfigureAwait(false);
            var hasWeapon = await OnFramework(() =>
                NpcInstance.TryReadLocalWeaponState(out _, out var has) && has).ConfigureAwait(false);

            var appearance = await OnFramework(() => NpcInstance.CaptureLocalPlayer(string.Empty)).ConfigureAwait(false);
            return new PlayerLook(state, meta, resolved, hasWeapon, appearance);
        }
        finally
        {
            if (forced)
                await OnFramework(() => NpcInstance.SetLocalWeaponDrawn(weaponState.Drawn)).ConfigureAwait(false);
        }
    }

    private static ImportResult Failure(string error) => new(null, null, null, 0, error);


    private ImportResult BuildFromResolved(string state, string meta, Dictionary<string, HashSet<string>> resolved)
    {
        var manifest = new NpcModManifest { Glamourer = state, Manipulation = meta };
        var byHash = new Dictionary<string, NpcModManifest.ModFile>(StringComparer.Ordinal);
        var rejected = 0;
        var buffer = new byte[128 * 1024];

        foreach (var (resolvedPath, gamePathSet) in resolved)
        {
            var gamePaths = gamePathSet.Select(g => g.ToLowerInvariant()).Where(NpcModManifest.IsValidGamePath)
                .Distinct(StringComparer.Ordinal).ToList();
            if (gamePaths.Count == 0) continue;

            if (Path.IsPathRooted(resolvedPath))
            {
                if (!TryCaptureFile(resolvedPath, gamePaths, buffer, byHash)) rejected++;
                continue;
            }

            var target = resolvedPath.ToLowerInvariant();
            if (gamePaths.Contains(target, StringComparer.Ordinal) || !NpcModManifest.IsValidGamePath(target)) continue;
            manifest.Swaps.Add(new NpcModManifest.ModSwap { Paths = gamePaths, Target = target });
        }

        manifest.Files.AddRange(byHash.Values);

        if (!manifest.Sanitize(out var error))
            return new ImportResult(null, null, null, rejected, error);

        var bytes = manifest.ToBytes();
        var id = NpcModManifest.ComputeId(bytes);
        store.SaveManifest(id, bytes);
        return new ImportResult(manifest, bytes, id, rejected, null);
    }

    private bool TryCaptureFile(string path, List<string> gamePaths, byte[] buffer,
        Dictionary<string, NpcModManifest.ModFile> byHash)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > NpcModManifest.MaxFileSize) return false;

            string hash;
            using (var stream = info.OpenRead())
                hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(stream)).ToLowerInvariant();

            if (byHash.TryGetValue(hash, out var existing))
            {
                foreach (var gamePath in gamePaths)
                    if (!existing.Paths.Contains(gamePath, StringComparer.Ordinal)) existing.Paths.Add(gamePath);
                return true;
            }

            var file = new NpcModManifest.ModFile { Paths = gamePaths, Hash = hash, Size = info.Length };
            if (!store.Contains(hash))
            {
                using var stream = info.OpenRead();
                if (!store.TryStore(hash, NpcModManifest.ExtensionFor(file), stream, info.Length, buffer))
                    return false;
            }

            byHash[hash] = file;
            return true;
        }
        catch (Exception ex)
        {
            log.Warning($"[NpcMods] Fichier ignoré ({Path.GetFileName(path)}) : {ex.Message}");
            return false;
        }
    }


    public ImportResult? LoadFromCache(string id)
    {
        var bytes = store.LoadManifest(id);
        if (bytes == null || NpcModManifest.ComputeId(bytes) != id) return null;

        var manifest = NpcModManifest.FromBytes(bytes, out _);
        if (manifest == null) return null;
        foreach (var file in manifest.Files)
            if (!store.Contains(file.Hash)) return null;

        return new ImportResult(manifest, bytes, id, 0, null);
    }


    public string? CheckLocalRequirements(NpcModManifest manifest)
    {
        if (!penumbra.Available && (manifest.Files.Count > 0 || manifest.Swaps.Count > 0
                                    || !string.IsNullOrEmpty(manifest.Manipulation)))
            return Loc.Get("Npc.ModsNeedPenumbra");
        if (!glamourer.Available && !string.IsNullOrEmpty(manifest.Glamourer))
            return Loc.Get("Npc.ModsNeedGlamourer");
        return null;
    }

    public void ApplyLocal(NpcInstance npc, NpcModManifest manifest, byte[] manifestBytes, string id)
    {
        npc.LocalModId = id;
        npc.ModState = NpcModState.Preparing;
        log.Info($"[NpcMods] '{npc.DisplayName}' : manifeste {id[..8]}, {manifest.Files.Count} fichier(s) "
                 + $"({manifest.TotalFileSize / 1024} Ko), {manifest.Swaps.Count} swap(s), "
                 + $"Glamourer {manifest.Glamourer.Length} car., méta {manifest.Manipulation.Length} car.");
        var cts = Track(npc);
        _ = Task.Run(async () =>
        {
            try
            {
                var ok = await ApplyCoreAsync(npc, manifest, captureAppearance: true, cts.Token).ConfigureAwait(false);
                if (!ok || cts.IsCancellationRequested) return;
                await PublishAsync(npc, manifest, manifestBytes, id, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                log.Error(ex, "[NpcMods] Application locale interrompue");
                await Fail(npc, ex.Message).ConfigureAwait(false);
            }
        });
    }


    public void PublishPending()
    {
        if (!transfer.CanPublish) return;

        foreach (var npc in npcManager.Instances)
        {
            if (npc.IsReplicated || !npc.IsAlive || npc.LocalModId == null) continue;

            switch (npc.ModState)
            {
                case NpcModState.LocalOnly:
                    RetryPublish(npc);
                    break;
                case NpcModState.Applied when npc.Mods != null:
                    EnsureFilesQuietly(npc);
                    break;
            }
        }
    }

    private void EnsureFilesQuietly(NpcInstance npc)
    {
        if (npc.LocalModId is not { } id || LoadFromCache(id) is not { Manifest: { } manifest, Bytes: { } bytes })
            return;

        var cts = Track(npc);
        _ = Task.Run(async () =>
        {
            try
            {
                var error = await transfer.PublishAsync(id, bytes, manifest, store, null, cts.Token).ConfigureAwait(false);
                if (error != null)
                    log.Warning($"[NpcMods] Vérification des fichiers de '{npc.DisplayName}' : {error}");
            }
            catch (OperationCanceledException) { }
        });
    }

    public void RetryPublish(NpcInstance npc)
    {
        if (npc.LocalModId is not { } id || LoadFromCache(id) is not { Manifest: { } manifest, Bytes: { } bytes })
            return;

        var cts = Track(npc);
        _ = Task.Run(async () =>
        {
            try { await PublishAsync(npc, manifest, bytes, id, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        });
    }

    private async Task PublishAsync(NpcInstance npc, NpcModManifest manifest, byte[] bytes, string id, CancellationToken token)
    {
        if (!transfer.CanPublish)
        {
            await SetState(npc, NpcModState.LocalOnly, Loc.Get("Npc.ModsLocalOnly")).ConfigureAwait(false);
            return;
        }

        await SetState(npc, NpcModState.Uploading, null).ConfigureAwait(false);
        var error = await transfer.PublishAsync(id, bytes, manifest, store, (done, total) =>
        {
            npc.ModProgress = total > 0 ? (float)done / total : 0f;
        }, token).ConfigureAwait(false);

        if (error != null)
        {
            await SetState(npc, NpcModState.LocalOnly, string.Format(Loc.Get("Npc.ModsUploadFailedFmt"), error))
                .ConfigureAwait(false);
            return;
        }

        await framework.RunOnFrameworkThread(() =>
        {
            npc.Mods = new NpcModRef { Id = id, TotalBytes = manifest.TotalFileSize, FileCount = manifest.Files.Count };
            npc.ModState = NpcModState.Applied;
            npc.ModStatus = null;
            npcManager.NotifyChanged();
        }).ConfigureAwait(false);
    }


    // Vrai si ce client va appliquer les mods d'un PNJ reçu. Sinon il affichera la version de base.
    public bool ShouldApplyRemote(NpcModRef? mods)
    {
        if (mods == null || !NpcModManifest.IsValidHash(mods.Id)) return false;
        if (!configuration.AcceptModdedNpcs) return false;
        if (!penumbra.Available) return false;
        if (mods.TotalBytes is < 0 or > MaxRemoteModBytes) return false;
        return transfer.CanDownload;
    }

    public void ApplyRemote(NpcInstance npc, NpcModRef mods)
    {
        npc.Mods = mods;
        npc.ModState = NpcModState.Preparing;
        var cts = Track(npc);

        _ = Task.Run(async () =>
        {

            _ = Task.Delay(VanillaFallbackDelay, cts.Token).ContinueWith(_ =>
                framework.RunOnFrameworkThread(() =>
                {
                    if (npc.IsAlive && !npc.DrawRequested) npc.RequestDraw();
                }), TaskContinuationOptions.OnlyOnRanToCompletion);

            try
            {
                await SetState(npc, NpcModState.Downloading, null).ConfigureAwait(false);

                var (bytes, error) = await transfer.DownloadManifestAsync(mods.Id, cts.Token).ConfigureAwait(false);
                if (bytes == null)
                {
                    await Fail(npc, error ?? "manifeste indisponible").ConfigureAwait(false);
                    return;
                }

                if (NpcModManifest.ComputeId(bytes) != mods.Id)
                {
                    await Fail(npc, "manifeste altéré").ConfigureAwait(false);
                    return;
                }

                var manifest = NpcModManifest.FromBytes(bytes, out error);
                if (manifest == null)
                {
                    await Fail(npc, error ?? "manifeste invalide").ConfigureAwait(false);
                    return;
                }

                var downloadError = await transfer.DownloadFilesAsync(manifest, store, (done, total) =>
                {
                    npc.ModProgress = total > 0 ? (float)done / total : 0f;
                }, cts.Token).ConfigureAwait(false);
                if (downloadError != null)
                {
                    await Fail(npc, downloadError).ConfigureAwait(false);
                    return;
                }

                await ApplyCoreAsync(npc, manifest, captureAppearance: false, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // PNJ retiré ou plugin déchargé.
            }
            catch (Exception ex)
            {
                log.Error(ex, "[NpcMods] Application distante interrompue");
                await Fail(npc, ex.Message).ConfigureAwait(false);
            }
        });
    }

    // Retire les mods d'un PNJ déjà affiché (le MJ est revenu à une version de base).
    public void RemoveMods(NpcInstance npc)
    {
        Cancel(npc.NetworkId);
        npc.Mods = null;
        npc.ModState = NpcModState.None;
        npc.ModStatus = null;
        ReleaseCollection(npc, redraw: true);
    }


    private async Task<bool> ApplyCoreAsync(NpcInstance npc, NpcModManifest manifest, bool captureAppearance,
        CancellationToken token)
    {
        await SetState(npc, NpcModState.Applying, null).ConfigureAwait(false);

        var index = await OnFramework(() => npc.TableIndex).ConfigureAwait(false);
        if (index < 0)
        {
            await Fail(npc, "PNJ introuvable dans la table d'objets").ConfigureAwait(false);
            return false;
        }

        var objectIndex = (ushort)index;
        var needsCollection = manifest.Files.Count > 0 || manifest.Swaps.Count > 0
                              || !string.IsNullOrEmpty(manifest.Manipulation);

        await OnFramework(() => ReleaseCollection(npc, redraw: false)).ConfigureAwait(false);

        if (needsCollection)
        {
            if (!penumbra.Available)
            {
                await Fail(npc, Loc.Get("Npc.ModsNeedPenumbra")).ConfigureAwait(false);
                return false;
            }

            var paths = BuildPaths(manifest, out var missing);
            if (missing > 0) log.Warning($"[NpcMods] {missing} fichier(s) absents du cache, ignorés.");

            var collection = await OnFramework(() =>
            {
                var id = penumbra.CreateCollection("MasterEvent_" + npc.NetworkId.ToString("N")[..8]);
                if (id == Guid.Empty) return Guid.Empty;

                var ok = paths.Count == 0 || penumbra.AddMod("MasterEvent_Files", id, paths, string.Empty);
                if (ok && !string.IsNullOrEmpty(manifest.Manipulation))
                    ok = penumbra.AddMod("MasterEvent_Meta", id, new Dictionary<string, string>(), manifest.Manipulation);
                if (ok) ok = penumbra.Assign(id, objectIndex);

                if (ok) return id;
                penumbra.DeleteCollection(id);
                return Guid.Empty;
            }).ConfigureAwait(false);

            if (collection == Guid.Empty)
            {
                await Fail(npc, Loc.Get("Npc.ModsPenumbraRefused")).ConfigureAwait(false);
                return false;
            }

            npc.TempCollection = collection;

            if (!await WaitForCollectionAsync(objectIndex, collection, token).ConfigureAwait(false))
            {
                await Fail(npc, Loc.Get("Npc.ModsCollectionTimeout")).ConfigureAwait(false);
                return false;
            }
        }

        // Dessin : seulement maintenant que la collection est effective.
        var drawn = await WaitForDrawAsync(npc, token).ConfigureAwait(false);
        if (!drawn)
        {
            await Fail(npc, Loc.Get("Npc.ModsDrawTimeout")).ConfigureAwait(false);
            return false;
        }

        // Laisse le modèle se charger avant d'imposer l'état Glamourer.
        await Task.Delay(700, token).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(manifest.Glamourer))
        {
            if (!glamourer.Available)
            {
                await Fail(npc, Loc.Get("Npc.ModsNeedGlamourer")).ConfigureAwait(false);
                return false;
            }

            string? glamError = null;
            var applied = await OnFramework(() => glamourer.TryApplyState(manifest.Glamourer, objectIndex, out glamError))
                .ConfigureAwait(false);
            if (!applied)
                log.Warning($"[NpcMods] Glamourer : {glamError}");
        }

        if (needsCollection || !string.IsNullOrEmpty(manifest.Glamourer))
        {
            await OnFramework(() => penumbra.Redraw(objectIndex)).ConfigureAwait(false);

            // Le redraw détruit puis recrée le modèle : l'état des armes n'a de sens qu'après.
            await Task.Delay(500, token).ConfigureAwait(false);
            await WaitUntilRenderedAsync(npc, token).ConfigureAwait(false);
        }

        // Le redraw remet la posture à zéro : on réaffirme l'arme dégainée voulue par le MJ, à
        // plusieurs reprises car le jeu la réinitialise encore pendant quelques images.
        for (var pass = 0; pass < 3; pass++)
        {
            await OnFramework(npc.ApplyWeaponDrawn).ConfigureAwait(false);
            await Task.Delay(pass == 0 ? 100 : 400, token).ConfigureAwait(false);
        }

        log.Info($"[NpcMods] '{npc.DisplayName}' armes : {await OnFramework(npc.DescribeWeapons).ConfigureAwait(false)}"
                 + $" (dégainée voulue : {npc.WeaponDrawn})");

        if (captureAppearance)
        {
            // La version de base d'un PNJ moddé est ce que Glamourer vient de dessiner : c'est le
            // repli le plus fidèle pour les clients qui n'appliquent pas les mods.
            await OnFramework(() =>
            {
                if (npc.CaptureCurrent() is { } captured)
                    npc.SetAppearanceSnapshot(captured);
            }).ConfigureAwait(false);
        }

        await SetState(npc, captureAppearance ? NpcModState.LocalOnly : NpcModState.Applied, null).ConfigureAwait(false);
        return true;
    }

    private Dictionary<string, string> BuildPaths(NpcModManifest manifest, out int missing)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        missing = 0;

        foreach (var file in manifest.Files)
        {
            if (!store.TryGetPath(file.Hash, out var local))
            {
                missing++;
                continue;
            }

            store.Touch(file.Hash);
            foreach (var gamePath in file.Paths) paths[gamePath] = local;
        }

        // Les remplacements de chemin passent après : un fichier explicitement redirigé l'emporte.
        foreach (var swap in manifest.Swaps)
            foreach (var gamePath in swap.Paths)
                paths[gamePath] = swap.Target;

        return paths;
    }

    // Attend que le modèle soit de nouveau rendu de façon stable (3 relevés consécutifs).
    private async Task WaitUntilRenderedAsync(NpcInstance npc, CancellationToken token)
    {
        var stable = 0;
        for (var waited = 0; waited < 10000 && stable < 3; waited += 100)
        {
            token.ThrowIfCancellationRequested();
            stable = await OnFramework(() => npc.IsRendered).ConfigureAwait(false) ? stable + 1 : 0;
            await Task.Delay(100, token).ConfigureAwait(false);
        }
    }

    private async Task<bool> WaitForCollectionAsync(ushort objectIndex, Guid expected, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + CollectionTimeout;
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            var effective = await OnFramework(() => penumbra.EffectiveCollection(objectIndex)).ConfigureAwait(false);
            if (effective == expected)
            {
                await Task.Delay(150, token).ConfigureAwait(false);
                return true;
            }

            await Task.Delay(50, token).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> WaitForDrawAsync(NpcInstance npc, CancellationToken token)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDrawn() => tcs.TrySetResult();

        var already = await OnFramework(() =>
        {
            if (npc.IsDrawn) return true;
            npc.Drawn += OnDrawn;
            if (!npc.DrawRequested) npc.RequestDraw();
            return false;
        }).ConfigureAwait(false);
        if (already) return true;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(DrawTimeout);
            try
            {
                await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return false;
            }
        }
        finally
        {
            npc.Drawn -= OnDrawn;
        }
    }


    private CancellationTokenSource Track(NpcInstance npc)
    {
        Cancel(npc.NetworkId);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        running[npc.NetworkId] = cts;

        // Abonnement unique : la libération du PNJ défait sa collection.
        npc.Released -= OnReleased;
        npc.Released += OnReleased;
        return cts;
    }

    private void Cancel(Guid networkId)
    {
        if (running.TryRemove(networkId, out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }
    }

    private void OnReleased(NpcInstance npc)
    {
        Cancel(npc.NetworkId);
        npc.Released -= OnReleased;

        // Glamourer garde un état par identité : sans cette remise à zéro, un PNJ qui reprendrait
        // le même nom hériterait de la tenue du précédent.
        if (npc.LocalModId != null || npc.Mods != null) glamourer.RevertByName(npc.IdentifierName);
        ReleaseCollection(npc, redraw: false);
    }

    private void ReleaseCollection(NpcInstance npc, bool redraw)
    {
        var collection = npc.TempCollection;
        if (collection == Guid.Empty) return;
        npc.TempCollection = Guid.Empty;

        // Redessiner avant de supprimer : sinon le PNJ garderait ses ressources moddées.
        if (redraw && npc.TableIndex is >= 0 and var idx) penumbra.Redraw((ushort)idx);
        penumbra.DeleteCollection(collection);
    }

    private Task<T> OnFramework<T>(Func<T> action) => framework.RunOnFrameworkThread(action);

    private Task OnFramework(Action action) => framework.RunOnFrameworkThread(action);

    private async Task SetState(NpcInstance npc, NpcModState state, string? status)
    {
        await framework.RunOnFrameworkThread(() =>
        {
            npc.ModState = state;
            npc.ModStatus = status;
            if (state is not (NpcModState.Uploading or NpcModState.Downloading)) npc.ModProgress = 0f;
            // Sortir d'un état « retenu » rend le PNJ diffusable : le MJ doit le rediffuser.
            if (!npc.IsReplicated) npcManager.NotifyChanged();
        }).ConfigureAwait(false);
    }

    // Échec : le PNJ est quand même affiché, dans sa version de base.
    private async Task Fail(NpcInstance npc, string reason)
    {
        log.Warning($"[NpcMods] '{npc.DisplayName}' : {reason}");
        await framework.RunOnFrameworkThread(() =>
        {
            ReleaseCollection(npc, redraw: npc.IsDrawn);
            npc.ModState = NpcModState.Failed;
            npc.ModStatus = reason;
            npc.ModProgress = 0f;
            if (npc.IsAlive && !npc.DrawRequested) npc.RequestDraw();
            if (!npc.IsReplicated) npcManager.NotifyChanged();
        }).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
        lifetime.Dispose();
        running.Clear();
    }
}

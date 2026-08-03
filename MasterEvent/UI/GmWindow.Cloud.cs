using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using MasterEvent.Localization;
using MasterEvent.Services;

namespace MasterEvent.UI;

// Section « Ashfall Connect » des réglages : liaison du compte et synchronisation
// des fiches et modèles.
public sealed partial class GmWindow
{
    private const string ConnectLinkUrl = "https://connect.ashfall-codex.dev/link";

    // Mêmes teintes que les autres onglets (cf. GmWindow.Models.cs) : le thème ne définit
    // que la couleur d'accent, les états sont exprimés localement.
    private static readonly Vector4 CloudMuted = new(0.6f, 0.6f, 0.6f, 1f);
    private static readonly Vector4 CloudSuccess = new(0.2f, 1f, 0.2f, 1f);
    private static readonly Vector4 CloudWarning = new(1f, 0.75f, 0.2f, 1f);
    private static readonly Vector4 CloudDanger = new(1f, 0.3f, 0.3f, 1f);

    private CancellationTokenSource? cloudCts;
    private string? cloudLinkCode;
    private DateTimeOffset cloudCodeExpiresAt;
    private bool cloudLinkInProgress;
    private string? cloudLinkedTo;
    private string? cloudError;
    private CloudSyncService.CloudStatus? cloudStatus;
    private DateTime cloudStatusFetchedAt = DateTime.MinValue;
    private bool cloudStatusInProgress;
    private bool cloudCodeCopied;
    private DateTime cloudCodeCopiedAt;

    private CloudSyncService? CloudSync => session.CloudSync;

    private void DrawCloudContent()
    {
        // Section des réglages : l'en-tête (icône, titre, sous-titre) est rendu par le cadre commun.
        DrawSectionHeader(CloudSettingsTab);

        ImGui.TextWrapped(Loc.Get("Cloud.Intro"));
        ImGui.Spacing();

        if (CloudSync is null)
        {
            ImGui.TextColored(CloudMuted, Loc.Get("Cloud.Unavailable"));
            return;
        }

        RefreshCloudStatusIfStale();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (cloudLinkCode is not null || cloudLinkInProgress || cloudLinkedTo is not null)
            DrawCloudLinkFlow();
        else
            DrawCloudAccountState();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawCloudSyncControls();
    }

    /// État courant : compte lié ou non, avec le bouton pour lancer la liaison.
    private void DrawCloudAccountState()
    {
        var identifier = configuration.MasterEventAccountId;
        var linked = cloudStatus?.Linked == true;

        if (linked)
        {
            ImGui.TextColored(CloudSuccess, Loc.Get("Cloud.Linked"));
            if (!string.IsNullOrEmpty(identifier))
            {
                ImGui.SameLine();
                ImGui.TextColored(CloudMuted, identifier);
            }
            ImGui.Spacing();
            ImGui.TextWrapped(Loc.Get("Cloud.LinkedHint"));
            ImGui.Spacing();

            if (ImGui.Button(Loc.Get("Cloud.OpenSite")))
                Util.OpenLink(ConnectLinkUrl);
            return;
        }

        ImGui.TextColored(CloudMuted, Loc.Get("Cloud.NotLinked"));
        ImGui.Spacing();
        ImGui.TextWrapped(Loc.Get("Cloud.NotLinkedHint"));
        ImGui.Spacing();

        if (ImGui.Button(Loc.Get("Cloud.LinkButton"), new Vector2(220f * ImGuiHelpers.GlobalScale, 0)))
            _ = StartCloudLinkAsync();

        if (!string.IsNullOrEmpty(cloudError))
        {
            ImGui.Spacing();
            ImGui.TextColored(CloudDanger, cloudError);
        }
    }

    /// Affichage du code à 8 caractères pendant que l'utilisateur le colle sur le site.
    private void DrawCloudLinkFlow()
    {
        if (cloudLinkInProgress)
        {
            ImGui.TextColored(CloudMuted, Loc.Get("Cloud.Generating"));
            return;
        }

        if (cloudLinkedTo is not null)
        {
            ImGui.TextColored(CloudSuccess, Loc.Get("Cloud.LinkSuccess"));
            ImGui.Spacing();
            ImGui.TextColored(CloudMuted,
                string.Format(Loc.Get("Cloud.LinkedTo"), cloudLinkedTo));
            ImGui.Spacing();
            if (ImGui.Button(Loc.Get("Cloud.Close")))
                ResetCloudLinkFlow();
            return;
        }

        if (cloudLinkCode is null) return;

        var displayed = cloudLinkCode.Length == 8
            ? $"{cloudLinkCode[..4]}-{cloudLinkCode[4..]}"
            : cloudLinkCode;

        using (ImRaii.PushFont(UiBuilder.MonoFont))
        using (ImRaii.PushColor(ImGuiCol.Text, MasterEventTheme.AccentColor))
        {
            ImGui.SetWindowFontScale(1.6f);
            ImGui.TextUnformatted(displayed);
            ImGui.SetWindowFontScale(1f);
        }

        var remaining = cloudCodeExpiresAt - DateTimeOffset.UtcNow;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        var secondsLeft = (int)remaining.TotalSeconds;
        var timerColor = secondsLeft < 60 ? CloudDanger
            : secondsLeft < 120 ? CloudWarning
            : CloudSuccess;

        ImGui.TextColored(timerColor, string.Format(Loc.Get("Cloud.CodeValidFor"), $"{remaining:mm\\:ss}"));
        ImGui.Spacing();

        if (ImGui.Button(Loc.Get("Cloud.CopyCode")))
        {
            ImGui.SetClipboardText(cloudLinkCode);
            cloudCodeCopied = true;
            cloudCodeCopiedAt = DateTime.UtcNow;
        }
        ImGui.SameLine();
        if (ImGui.Button(Loc.Get("Cloud.OpenSite")))
            Util.OpenLink(ConnectLinkUrl);
        ImGui.SameLine();
        if (ImGui.Button(Loc.Get("Cloud.Cancel")))
            ResetCloudLinkFlow();

        if (cloudCodeCopied)
        {
            if ((DateTime.UtcNow - cloudCodeCopiedAt).TotalSeconds < 2)
            {
                ImGui.Spacing();
                ImGui.TextColored(CloudSuccess, Loc.Get("Cloud.Copied"));
            }
            else cloudCodeCopied = false;
        }

        ImGui.Spacing();
        ImGui.TextColored(MasterEventTheme.AccentColor, Loc.Get("Cloud.Steps"));
        ImGui.BulletText(Loc.Get("Cloud.Step1"));
        ImGui.BulletText(Loc.Get("Cloud.Step2"));
        ImGui.BulletText(Loc.Get("Cloud.Step3"));

        if (secondsLeft == 0)
        {
            cloudLinkCode = null;
            cloudError = Loc.Get("Cloud.CodeExpired");
        }
    }

    /// Interrupteur global et synchronisation manuelle.
    private void DrawCloudSyncControls()
    {
        var enabled = configuration.CloudSyncEnabled;
        if (ImGui.Checkbox(Loc.Get("Cloud.SyncEnabled"), ref enabled))
        {
            configuration.CloudSyncEnabled = enabled;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.Get("Cloud.SyncEnabledTooltip"));

        ImGui.Spacing();

        var canSync = CloudSync?.IsActive == true && CloudSync?.IsBusy != true;
        if (!canSync) ImGui.BeginDisabled();
        if (ImGui.Button(Loc.Get("Cloud.SyncNow"), new Vector2(220f * ImGuiHelpers.GlobalScale, 0)))
            _ = CloudSync?.SyncAsync();
        if (!canSync) ImGui.EndDisabled();

        if (CloudSync?.IsBusy == true)
        {
            ImGui.SameLine();
            ImGui.TextColored(CloudMuted, Loc.Get("Cloud.Syncing"));
        }

        var lastSync = configuration.CloudLastSyncAt;
        if (lastSync > 0)
        {
            ImGui.Spacing();
            var when = DateTimeOffset.FromUnixTimeMilliseconds(lastSync).ToLocalTime();
            ImGui.TextColored(CloudMuted,
                string.Format(Loc.Get("Cloud.LastSync"), when.ToString("g")));
        }

        var error = CloudSync?.LastError;
        if (!string.IsNullOrEmpty(error))
        {
            ImGui.Spacing();
            ImGui.TextColored(CloudDanger, string.Format(Loc.Get("Cloud.SyncError"), error));
        }
    }

    private async Task StartCloudLinkAsync()
    {
        if (CloudSync is null) return;

        ResetCloudLinkFlow();
        cloudCts = new CancellationTokenSource();
        cloudLinkInProgress = true;
        cloudError = null;

        try
        {
            // L'alias n'est qu'un libellé d'affichage côté site : le nom du personnage courant suffit.
            var alias = Plugin.ObjectTable.LocalPlayer?.Name.ToString();
            var result = await CloudSync.GenerateLinkCodeAsync(alias, cloudCts.Token).ConfigureAwait(false);
            if (result is null)
            {
                cloudError = CloudSync.LastError ?? Loc.Get("Cloud.Error.Generic");
                return;
            }

            cloudLinkCode = result.Code;
            cloudCodeExpiresAt = result.ExpiresAt;
            _ = PollCloudLinkStatusAsync(cloudCts.Token);
        }
        catch (OperationCanceledException) { /* annulation attendue */ }
        catch (Exception ex)
        {
            cloudError = ex.Message;
        }
        finally
        {
            cloudLinkInProgress = false;
        }
    }

    /// Attend que l'utilisateur ait collé le code sur le site, puis pousse tout le contenu local
    /// pour que la page ne s'ouvre pas sur un coffre vide.
    private async Task PollCloudLinkStatusAsync(CancellationToken token)
    {
        var code = cloudLinkCode;
        if (CloudSync is null || string.IsNullOrEmpty(code)) return;

        try
        {
            while (!token.IsCancellationRequested
                   && string.Equals(cloudLinkCode, code, StringComparison.Ordinal)
                   && cloudLinkedTo is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);

                var status = await CloudSync.GetLinkStatusAsync(code, token).ConfigureAwait(false);
                if (status is null) continue;

                if (string.Equals(status.Status, "consumed", StringComparison.Ordinal))
                {
                    cloudLinkedTo = status.LinkedTo ?? Loc.Get("Cloud.YourAccount");
                    cloudStatusFetchedAt = DateTime.MinValue; // force un rafraîchissement de l'état
                    await CloudSync.PushEverythingAsync(token).ConfigureAwait(false);
                    return;
                }
                if (string.Equals(status.Status, "expired", StringComparison.Ordinal))
                {
                    cloudLinkCode = null;
                    cloudError = Loc.Get("Cloud.CodeExpired");
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* annulation attendue */ }
    }

    private void RefreshCloudStatusIfStale()
    {
        if (CloudSync is null || cloudStatusInProgress) return;
        if ((DateTime.UtcNow - cloudStatusFetchedAt).TotalSeconds < 60) return;

        cloudStatusInProgress = true;
        cloudStatusFetchedAt = DateTime.UtcNow;

        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                cloudStatus = await CloudSync.GetStatusAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Plugin.Log.Debug($"[Cloud] État indisponible : {ex.Message}");
            }
            finally
            {
                cloudStatusInProgress = false;
            }
        });
    }

    private void ResetCloudLinkFlow()
    {
        cloudCts?.Cancel();
        cloudCts?.Dispose();
        cloudCts = null;
        cloudLinkCode = null;
        cloudLinkedTo = null;
        cloudLinkInProgress = false;
        cloudCodeCopied = false;
    }
}

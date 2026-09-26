using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using MasterEvent.Localization;
using MasterEvent.Services;

namespace MasterEvent.UI;

public sealed class NotesWindow : MasterEventWindowBase
{
    private readonly NotesStore store;
    private readonly Configuration configuration;

    // Retour du dernier import/export, affiché à côté des boutons.
    private string? lastMessage;
    private bool lastMessageIsError;

    public NotesWindow(NotesStore store, Configuration configuration)
        : base($"{Loc.Get("Notes.Title")}###MasterEventNotes")
    {
        this.store = store;
        this.configuration = configuration;

        Size = new Vector2(420f, 380f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(260f, 180f),
            MaximumSize = new Vector2(1400f, 1400f),
        };
    }

    protected override void DrawContents()
    {
        var icon = FontAwesomeIcon.StickyNote.ToIconString();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(MasterEventTheme.AccentColor, icon);
        ImGui.SameLine();
        ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("Notes.Hint"));

        ImGuiHelpers.ScaledDummy(4f);
        DrawImportExport();
        ImGuiHelpers.ScaledDummy(4f);

        // La zone de saisie occupe tout l'espace restant, moins la ligne d'état du bas.
        var avail = ImGui.GetContentRegionAvail();
        var footerH = ImGui.GetTextLineHeightWithSpacing() + 4f * ImGuiHelpers.GlobalScale;
        var boxSize = new Vector2(avail.X, MathF.Max(60f, avail.Y - footerH));

        var text = store.Text;
        if (ImGui.InputTextMultiline("##me_notes", ref text, NotesStore.MaxChars, boxSize))
            store.SetText(text);

        DrawFooter();
    }

    private void DrawImportExport()
    {
        if (ImGui.Button(Loc.Get("Notes.Import") + "##me_notes_import"))
        {
            Plugin.FileDialogManager.OpenFileDialog(
                title: Loc.Get("Notes.ImportTitle"),
                filters: "Texte{.txt}",
                callback: (success, paths) =>
                {
                    if (!success) return;
                    if (paths.FirstOrDefault() is not { } path || string.IsNullOrEmpty(path)) return;
                    ImportFrom(path);
                },
                selectionCountMax: 1,
                startPath: null);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Loc.Get("Notes.ImportTooltip"));
            ImGui.EndTooltip();
        }

        ImGui.SameLine();

        if (ImGui.Button(Loc.Get("Notes.Export") + "##me_notes_export"))
        {
            Plugin.FileDialogManager.SaveFileDialog(
                title: Loc.Get("Notes.ExportTitle"),
                filters: "Texte{.txt}",
                defaultFileName: "notes.txt",
                defaultExtension: ".txt",
                callback: (success, path) =>
                {
                    if (!success || string.IsNullOrEmpty(path)) return;
                    ExportTo(path);
                });
        }

        if (lastMessage is { } message)
        {
            ImGui.SameLine();
            ImGui.TextColored(lastMessageIsError
                ? new Vector4(0.9f, 0.35f, 0.35f, 1f)
                : new Vector4(0.45f, 0.75f, 0.45f, 1f), message);
        }
    }

    // L'import REMPLACE le contenu : c'est le comportement attendu d'un « importer », et le
    // texte écrasé reste récupérable via l'export qu'on invite à faire d'abord.
    private void ImportFrom(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            var truncated = text.Length > NotesStore.MaxChars;

            store.SetText(text);
            SetMessage(truncated
                ? string.Format(Loc.Get("Notes.ImportedTruncated"), NotesStore.MaxChars)
                : Loc.Get("Notes.Imported"), isError: false);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Notes] Import de '{path}' impossible : {ex.Message}");
            SetMessage(Loc.Get("Notes.ImportFailed"), isError: true);
        }
    }

    private void ExportTo(string path)
    {
        try
        {
            // Le sélecteur n'impose pas toujours l'extension selon la saisie de l'utilisateur.
            if (!path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) path += ".txt";

            File.WriteAllText(path, store.Text);
            SetMessage(Loc.Get("Notes.Exported"), isError: false);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Notes] Export vers '{path}' impossible : {ex.Message}");
            SetMessage(Loc.Get("Notes.ExportFailed"), isError: true);
        }
    }

    private void SetMessage(string message, bool isError)
    {
        lastMessage = message;
        lastMessageIsError = isError;
    }

    private void DrawFooter()
    {
        var used = store.Text.Length;
        var ratio = used / (float)NotesStore.MaxChars;
        var countColor = ratio switch
        {
            >= 1f => MasterEventTheme.DangerColor,
            >= 0.8f => new Vector4(1f, 0.65f, 0.2f, 1f),
            _ => new Vector4(0.55f, 0.55f, 0.55f, 1f),
        };
        ImGui.TextColored(countColor, $"{used} / {NotesStore.MaxChars}");

        ImGui.SameLine();

        // État de sauvegarde : l'écriture étant différée de deux secondes, il faut dire à
        // l'utilisateur que sa frappe n'est pas encore sur le disque.
        if (store.HasUnsavedChanges)
        {
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f), Loc.Get("Notes.Saving"));
        }
        else
        {
            var synced = configuration.CloudSyncEnabled
                         && !string.IsNullOrEmpty(configuration.MasterEventAccountId);
            ImGui.TextColored(new Vector4(0.45f, 0.75f, 0.45f, 1f),
                Loc.Get(synced ? "Notes.SavedSynced" : "Notes.Saved"));
        }
    }
}

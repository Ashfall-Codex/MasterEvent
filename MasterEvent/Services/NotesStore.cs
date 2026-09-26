using System;
using System.IO;
using MasterEvent.Models;

namespace MasterEvent.Services;


public sealed class NotesStore : IDisposable
{
    private const double AutoSaveDelaySeconds = 2.0;
    public const int MaxChars = 20000;

    private readonly string path;
    private NotesDocument document;
    private bool dirty;
    private DateTime lastEdit;

    public Action? OnFlushed { get; set; }

    public NotesStore(string pluginConfigDir)
    {
        path = Path.Combine(pluginConfigDir, "notes.json");
        document = JsonFileStore.TryLoad<NotesDocument>(path) ?? new NotesDocument();
    }

    public string Text => document.Text;
    public bool HasUnsavedChanges => dirty;

    public void SetText(string value)
    {
        if (value.Length > MaxChars) value = value[..MaxChars];
        if (value == document.Text) return;

        document.Text = value;
        document.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        dirty = true;
        lastEdit = DateTime.UtcNow;
    }

    public void ApplyRemote(NotesDocument remote)
    {
        document = remote;
        document.Name = NotesDocument.DefaultName;
        dirty = false;
        JsonFileStore.Save(path, document);
    }

    public NotesDocument Snapshot() => document.DeepCopy();

    /// Appelé chaque frame : n'écrit qu'après une pause de saisie.
    public void Tick()
    {
        if (!dirty) return;
        if ((DateTime.UtcNow - lastEdit).TotalSeconds < AutoSaveDelaySeconds) return;
        Flush();
    }

    public void Flush()
    {
        if (!dirty) return;
        dirty = false;
        JsonFileStore.Save(path, document);
        OnFlushed?.Invoke();
    }

    // Écrit ce qui reste en attente : fermer le jeu juste après avoir tapé ne doit rien perdre.
    public void Dispose() => Flush();
}

using System;

namespace MasterEvent.Models;

[Serializable]
public class NotesDocument
{
    public const string DefaultName = "Notes";
    public string Name { get; set; } = DefaultName;
    public string Text { get; set; } = string.Empty;
    public long UpdatedAt { get; set; }
    public NotesDocument DeepCopy() => new()
    {
        Name = Name,
        Text = Text,
        UpdatedAt = UpdatedAt,
    };
}

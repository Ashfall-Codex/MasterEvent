using System;

namespace MasterEvent.Models;

[Serializable]
public class SharedTemplate
{
    public string Code { get; set; } = string.Empty;
    public string TemplateName { get; set; } = string.Empty;
    public bool Permanent { get; set; }
    public DateTime SharedAt { get; set; } = DateTime.Now;
}

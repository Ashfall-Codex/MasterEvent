using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;

namespace MasterEvent.Communication.Remote;

// Lit une commande de la page sur le thread réseau, sans rien toucher du jeu. Forme et bornes seulement
// (en-tête de RemoteViewMessages.cs) : la cible et les droits sont vérifiés au moment d'agir.
public static class RemoteCommandReader
{
    public const int MaxIdLength = 32;
    public const int MaxRefLength = 64;
    public const int MaxHashes = 8;
    public const int MaxSerial = 1_000_000;
    public const int MaxPoints = 99_999;

    // Racine, puis hashes : rien de plus profond n'est attendu.
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 4 };

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "type", "id", "op", "lobby", "seen", "encounter", "round", "kind", "ref", "group", "value",
        "field", "delta", "set", "stat", "player", "threshold", "hash", "hashes", "text",
    };

    // id est rempli dès qu'il est lisible, même pour une commande refusée : la réponse part avec lui.
    public static bool TryRead(ReadOnlyMemory<byte> json, [NotNullWhen(true)] out RemoteCommand? command,
        out string? id, out string? error)
    {
        command = null;
        id = null;
        error = RemoteErrors.BadArgs;
        try
        {
            using var document = JsonDocument.Parse(json, Options);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;

            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var duplicated = false;
            var ids = 0;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!Known.Contains(property.Name)) continue;
                if (property.NameEquals("id")) ids++;
                if (!fields.TryAdd(property.Name, property.Value)) duplicated = true;
            }

            // Deux id : impossible de savoir à quelle commande répondre.
            if (ids == 1 && fields["id"] is { ValueKind: JsonValueKind.String } rawId
                && rawId.GetString() is { } candidate && IsValidId(candidate))
                id = candidate;
            if (duplicated || id is null) return false;

            var read = new Fields(fields);
            if (!read.Constant("type", "cmd") || !read.Str("op", out var op)) return false;
            if (!RemoteOps.All.Contains(op) && !RemoteOps.Self.Contains(op))
            {
                error = RemoteErrors.UnknownOp;
                return false;
            }
            if (!read.Str("lobby", out var lobby) || !read.OptionalLong("seen", out var seen)) return false;

            var head = new RemoteCommand { Id = id, Op = op, Lobby = lobby, Seen = seen };
            command = op switch
            {
                RemoteOps.TurnStart => head,
                RemoteOps.TurnEnd => ReadTurnTarget(read, head),
                RemoteOps.TurnNextRound => ReadSerial(read, head),
                RemoteOps.TurnSetActed => ReadTurnTarget(read, head) is { } turn && read.Bool("value", out var value)
                    ? turn with { Value = value }
                    : null,
                RemoteOps.Vitals => ReadVitals(read, head),
                RemoteOps.Roll => read.Str("kind", out var kind) && kind is "marker" or "npc"
                                  && read.Str("ref", out var target) && read.OptionalStr("stat", out var stat)
                    ? head with { Kind = kind, Ref = target, Stat = stat }
                    : null,
                RemoteOps.RollRequest => read.Str("player", out var player) && read.OptionalStr("stat", out var asked)
                                         && read.Int("threshold", -MaxPoints, MaxPoints, out var threshold)
                    ? head with { Player = player, Stat = asked, Threshold = threshold }
                    : null,
                RemoteOps.Admit => read.Str("hash", out var admitted) ? head with { Hash = admitted } : null,
                RemoteOps.Deny => ReadDeny(read, head),
                RemoteOps.Announce => read.Text("text", out var text) ? head with { Text = text } : null,
                RemoteOps.SelfRoll => read.OptionalStr("stat", out var own) ? head with { Stat = own } : null,
                RemoteOps.SelfAnswer or RemoteOps.SelfEndTurn or RemoteOps.SelfDismiss => head,
                _ => null,
            };
            if (command is null) return false;

            error = null;
            return true;
        }
        // InvalidOperationException : surrogate isolé dans une chaîne, que GetString refuse.
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            command = null;
            return false;
        }
    }

    public static bool IsValidId(string id)
    {
        if (id.Length is < 1 or > MaxIdLength) return false;
        foreach (var c in id)
        {
            if (c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-')) return false;
        }
        return true;
    }

    private static RemoteCommand? ReadSerial(Fields read, RemoteCommand head)
        => read.Int("encounter", 0, MaxSerial, out var encounter) && read.Int("round", 0, MaxSerial, out var round)
            ? head with { Encounter = encounter, Round = round }
            : null;

    // group, ou bien kind et ref : une page qui enverrait les deux ne dirait pas laquelle viser.
    private static RemoteCommand? ReadTurnTarget(Fields read, RemoteCommand head)
    {
        if (ReadSerial(read, head) is not { } turn) return null;
        if (read.Has("group"))
        {
            return !read.Has("kind") && !read.Has("ref") && read.Str("group", out var group)
                ? turn with { Group = group }
                : null;
        }
        return read.Str("kind", out var kind) && IsKind(kind) && read.Str("ref", out var target)
            ? turn with { Kind = kind, Ref = target }
            : null;
    }

    private static RemoteCommand? ReadVitals(Fields read, RemoteCommand head)
    {
        if (!read.Str("kind", out var kind) || !IsKind(kind) || !read.Str("ref", out var target)
            || !read.Str("field", out var field) || field is not ("hp" or "mp" or "shield"))
            return null;

        var hasDelta = read.Has("delta");
        if (hasDelta == read.Has("set")) return null;
        if (hasDelta)
        {
            return read.Int("delta", -MaxPoints, MaxPoints, out var delta) && delta != 0
                ? head with { Kind = kind, Ref = target, Field = field, Delta = delta }
                : null;
        }
        return read.Int("set", 0, MaxPoints, out var set)
            ? head with { Kind = kind, Ref = target, Field = field, Set = set }
            : null;
    }

    private static RemoteCommand? ReadDeny(Fields read, RemoteCommand head)
    {
        if (!read.Str("hash", out var hash) || !read.Strings("hashes", MaxHashes, out var hashes)) return null;
        return hashes.Contains(hash) ? head with { Hash = hash, Hashes = hashes } : null;
    }

    private static bool IsKind(string kind) => kind is "player" or "marker" or "npc";

    // Une valeur JSON null vaut un champ absent : seuls les champs facultatifs l'acceptent.
    private readonly struct Fields(Dictionary<string, JsonElement> fields)
    {
        public bool Has(string key) => fields.TryGetValue(key, out var v) && v.ValueKind != JsonValueKind.Null;

        public bool Constant(string key, string expected)
            => fields.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String && v.ValueEquals(expected);

        public bool Str(string key, [NotNullWhen(true)] out string? value)
        {
            value = fields.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return value is { Length: >= 1 and <= MaxRefLength };
        }

        public bool OptionalStr(string key, out string? value)
        {
            value = null;
            return !Has(key) || Str(key, out value);
        }

        // Le texte d'une annonce n'a pas d'autre borne ici que la taille du message : il est mesuré après normalisation.
        public bool Text(string key, [NotNullWhen(true)] out string? value)
        {
            value = fields.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return value is not null;
        }

        // Un nombre décimal ou avec exposant n'est pas un entier : TryGetInt32 le refuse.
        public bool Int(string key, int min, int max, out int value)
        {
            value = 0;
            return fields.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number
                   && v.TryGetInt32(out value) && value >= min && value <= max;
        }

        public bool OptionalLong(string key, out long value)
        {
            value = 0;
            if (!Has(key)) return true;
            return fields[key].ValueKind == JsonValueKind.Number && fields[key].TryGetInt64(out value) && value >= 0;
        }

        public bool Bool(string key, out bool value)
        {
            value = false;
            if (!fields.TryGetValue(key, out var v) || v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return false;
            value = v.GetBoolean();
            return true;
        }

        public bool Strings(string key, int maxCount, [NotNullWhen(true)] out string[]? values)
        {
            values = null;
            if (!fields.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Array) return false;
            var count = v.GetArrayLength();
            if (count is < 1 || count > maxCount) return false;

            var result = new string[count];
            var i = 0;
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: >= 1 and <= MaxRefLength } text)
                    return false;
                result[i++] = text;
            }
            values = result;
            return true;
        }
    }
}

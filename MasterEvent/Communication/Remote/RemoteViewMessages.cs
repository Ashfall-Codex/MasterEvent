using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace MasterEvent.Communication.Remote;

// Messages entre le plugin et la page du second écran, servie sur 127.0.0.1. Version 2 : la page peut
// aussi commander la scène.
//
// Plugin vers page, dans cet ordre :
//   hello   dès l'ouverture du WebSocket, avant l'authentification : version (v), textes de la page,
//           sans donnée de séance
//   welcome après un ticket valide : jeton de session que la page garde en sessionStorage pour un F5
//   ack     réponse à une commande, envoyée à la seule connexion qui l'a émise
//   state   instantané complet de la scène, renvoyé seulement quand il change : 4 par seconde au plus,
//           et aussitôt après une commande qui l'a changé
//   notes   texte des notes du MJ, renvoyé seulement quand il change (une fois par seconde au plus)
//   bye     fermeture voulue : « revoked » (le MJ a coupé), « ended » (coupure d'office : plus de lobby
//           ou plus de rôle de MJ) ou « unloaded » (plugin déchargé)
// Page vers plugin :
//   auth    { ticket } au premier passage, { session } ensuite
//   cmd     une commande, une fois authentifiée
// Codes de fermeture : 4401 ticket ou session refusés, 4408 pas d'authentification à temps,
// 4409 trop de sessions, 1008 plus de 20 messages par seconde, 1009 message de plus de 16 Kio.
// Après bye : 1000 pour « revoked » et « ended », 1001 pour « unloaded ».
//
// Commande : { type: "cmd", id, op, lobby, seen, ... }, lue par RemoteCommandReader.
//   id      1 à 32 caractères parmi A-Z a-z 0-9 _ -, jamais réutilisé par la page. Le plugin retient les
//           64 derniers de chaque session, onglets confondus, et répond « duplicate » à un id déjà reçu.
//   op      une des RemoteOps ; toute autre valeur reçoit « unknownOp ».
//   lobby   state.link.lobbyCode recopié : une commande partie d'un autre lobby reçoit « stale ».
//   seen    seq du dernier state affiché au moment du clic, facultatif ; sert au journal, jamais de garde.
//   Une clé connue en double, une valeur du mauvais type ou hors bornes, un champ requis absent : « badArgs ».
//   Une clé inconnue est ignorée. Sans id lisible, rien n'est répondu.
//
//   op              champs                                  garde contre la latence et le double envoi
//   turnStart       aucun                                   sans effet si un combat est en cours
//   turnEnd         encounter, round, cible                 la cible a la main et n'a pas encore joué
//   turnNextRound   encounter, round                        tout le monde a joué ; sans effet si déjà passé
//   turnSetActed    encounter, round, cible, value          sans effet si la case vaut déjà value
//   vitals          kind, ref, field, et delta ou set       aucune : delta est un écart, set une valeur
//   roll            kind (marker ou npc), ref, stat
//   rollRequest     player, stat, threshold                 une demande par joueur toutes les 2 s
//   admit           hash
//   deny            hash, hashes                            le sous-groupe n'a pas grandi depuis la confirmation
//   announce        text                                    une annonce toutes les 3 s
//   cible : group (RemoteTurnEntry.GroupId), ou bien kind et ref, jamais les deux.
//   encounter : RemoteTurn.Encounter ; un combat terminé puis relancé change de numéro.
//   kind : « player », « marker » ou « npc » ; ref : RemotePlayer.Hash, RemoteMarker.Id ou RemoteNpc.Id.
//   field : « hp », « mp » ou « shield ». stat : RemoteStat.Id, null ou absent pour un jet sans stat.
//   hash : le représentant du sous-groupe (son premier membre) ; hashes : les membres que la page a montrés
//   en demandant confirmation, hash compris, 8 au plus.
//   text : 180 caractères au plus après Trim, 4 lignes au plus, aucun caractère de contrôle hors saut de ligne.
//   Bornes : chaînes de 1 à 64 caractères (text excepté) ; encounter et round de 0 à 1 000 000 ;
//   delta de -99 999 à 99 999, non nul ; set de 0 à 99 999 ; threshold de -99 999 à 99 999.
//
// Réponse : { type: "ack", id, ok, error, changed, seq }, une par commande. error (RemoteErrors) n'est
// présent qu'en cas de refus. changed est faux pour une commande déjà satisfaite, ou refusée. seq est celui
// du dernier state publié : après un changement, ce state contient déjà l'effet de la commande.
// Exécution sur le thread du jeu, dans l'ordre d'arrivée : 32 commandes en file, 4 par image et 6 par
// seconde en tout, sinon « busy ». Une commande plus vieille que 2,5 s reçoit « expired » et ne fait rien.
// Ce que la page peut envoyer est dans state.caps, recalculé avant chaque commande.
//
// Ce format ne porte jamais le LeaderToken, le fileToken, la clé de salle ni l'identifiant ME- :
// GmSnapshotBuilder ne les lit pas.
public static class RemoteView
{
    public const int Version = 2;

    // Les lettres accentuées partent telles quelles, sans séquence d'échappement ; < > & ' et " restent échappés.
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };
}

// PrivateByDefault : un onglet sans choix enregistré s'ouvre le privé masqué (réglage du MJ).
public sealed record RemoteHello(int V, string PluginVersion, string Lang, bool PrivateByDefault,
    IReadOnlyDictionary<string, string> Strings)
{
    public string Type => "hello";
}

public sealed record RemoteWelcome(string Session)
{
    public string Type => "welcome";
}

public sealed record RemoteBye(string Reason)
{
    public string Type => "bye";
}

public sealed record RemoteNotes(string Text)
{
    public string Type => "notes";
}

// Error : null quand Ok.
public sealed record RemoteAck(string Id, bool Ok, string? Error, bool Changed, long Seq)
{
    public string Type => "ack";
}

public static class RemoteOps
{
    public const string TurnStart = "turnStart";
    public const string TurnEnd = "turnEnd";
    public const string TurnNextRound = "turnNextRound";
    public const string TurnSetActed = "turnSetActed";
    public const string Vitals = "vitals";
    public const string Roll = "roll";
    public const string RollRequest = "rollRequest";
    public const string Admit = "admit";
    public const string Deny = "deny";
    public const string Announce = "announce";

    public static readonly IReadOnlyList<string> All =
        [TurnStart, TurnEnd, TurnNextRound, TurnSetActed, Vitals, Roll, RollRequest, Admit, Deny, Announce];

    // Page joueur, servie par son propre serveur (RemotePlayerMessages.cs). Absentes de All : la page MJ les
    // lit puis les refuse « forbidden », comme le serveur joueur refuse celles du MJ.
    //   selfRoll    stat facultatif     selfAnswer, selfEndTurn, selfDismiss    aucun champ
    public const string SelfRoll = "selfRoll";
    public const string SelfAnswer = "selfAnswer";
    public const string SelfEndTurn = "selfEndTurn";
    public const string SelfDismiss = "selfDismiss";

    public static readonly IReadOnlyList<string> Self = [SelfRoll, SelfAnswer, SelfEndTurn, SelfDismiss];
}

public static class RemoteErrors
{
    // Rôle insuffisant au moment d'agir, ou op absente de caps.
    public const string Forbidden = "forbidden";
    // Le rôle permettrait l'op, mais le relais n'est pas en séance.
    public const string Offline = "offline";
    public const string NoCombat = "noCombat";
    // L'état a changé depuis le clic (combat, round, acteur, lobby, sous-groupe) : rien n'a été fait.
    public const string Stale = "stale";
    // Cible absente : joueur parti, marqueur vidé, PNJ disparu, demande d'accès déjà traitée.
    public const string Gone = "gone";
    // Entrée groupée visée seule : c'est le groupe qui se coche.
    public const string Grouped = "grouped";
    // Jauge masquée par le modèle (PM, bouclier).
    public const string Hidden = "hidden";
    // PM d'un PNJ, qui n'en a pas.
    public const string NoField = "noField";
    public const string Unnamed = "unnamed";
    public const string UnknownStat = "unknownStat";
    public const string Empty = "empty";
    public const string TooLong = "tooLong";
    public const string TooManyLines = "tooManyLines";
    // Champ manquant, en double ou hors bornes, cible interdite (demande de jet au MJ).
    public const string BadArgs = "badArgs";
    public const string TargetOffline = "targetOffline";
    // Annonce moins de 3 s après la précédente, demande de jet au même joueur moins de 2 s après.
    public const string TooSoon = "tooSoon";
    // File pleine ou budget de commandes dépassé.
    public const string Busy = "busy";
    public const string Duplicate = "duplicate";
    public const string UnknownOp = "unknownOp";
    public const string Expired = "expired";
    // Erreur imprévue pendant l'exécution, détaillée au journal.
    public const string Internal = "internal";
}

// Page vers plugin, construit par RemoteCommandReader : chaque op a ses champs requis, déjà vérifiés et
// bornés ; les autres gardent leur valeur par défaut.
public sealed record RemoteCommand
{
    public required string Id { get; init; }
    public required string Op { get; init; }
    public required string Lobby { get; init; }
    public long Seen { get; init; }
    public int Encounter { get; init; }
    public int Round { get; init; }
    public string? Kind { get; init; }
    public string? Ref { get; init; }
    public string? Group { get; init; }
    public bool Value { get; init; }
    public string? Field { get; init; }
    public int? Delta { get; init; }
    public int? Set { get; init; }
    public string? Stat { get; init; }
    public string? Player { get; init; }
    public int Threshold { get; init; }
    public string? Hash { get; init; }
    public IReadOnlyList<string> Hashes { get; init; } = [];
    public string? Text { get; init; }
}

// Access : « gm » (MJ de la scène ou MJ joueur), « coGm » (promu : sans les stats des marqueurs,
// que le relais ne lui transmet pas), « none » (rôle perdu : tout le reste est vide).
// Caps : les op que la page peut envoyer maintenant, vide pour une page en lecture seule (hors lobby,
// relais hors séance, ou sans rôle). Le co-MJ n'a que roll, sur un marqueur et sans stat.
// Rules : accès « gm » seulement.
public sealed record RemoteState
{
    public string Type => "state";
    public long Seq { get; init; }
    public string Access { get; init; } = "none";
    public IReadOnlyList<string> Caps { get; init; } = [];
    public RemoteRules? Rules { get; init; }
    public RemoteLink Link { get; init; } = new();
    public RemoteDisplay Display { get; init; } = new();
    public RemotePrimary? Primary { get; init; }
    public IReadOnlyList<RemotePlayer> Party { get; init; } = [];
    public IReadOnlyList<RemoteMarker> Markers { get; init; } = [];
    public IReadOnlyList<RemoteNpc> Npcs { get; init; } = [];
    public RemoteTurn? Turn { get; init; }
    public IReadOnlyList<RemotePendingGroup> Pending { get; init; } = [];
    public IReadOnlyList<RemoteRoll> Rolls { get; init; } = [];
    public RemoteAmbiance Ambiance { get; init; } = new();
}

// State : « offline », « reconnecting », « serverOnly » ou « inSession » (RelayLinkState).
// Suspended : relais coupé volontairement en instance ; la page, elle, reste vivante.
// PartyShown et PartyOnline : joueurs comptés par la barre de séance (le MJ seulement s'il joue), puis
// ceux qui sont connectés. Remplis quel que soit l'accès : le co-MJ ne reçoit pas la liste Party.
public sealed record RemoteLink
{
    public string State { get; init; } = "offline";
    public string? LobbyCode { get; init; }
    public int OnlinePlayers { get; init; }
    public bool Suspended { get; init; }
    public int PartyShown { get; init; }
    public int PartyOnline { get; init; }
}

// HpMode et MpMode : « points » ou « percent ». HpLabel et MpLabel : noms des jauges choisis par le
// modèle actif. StatTarget : modèle à seuils, où une stat s'affiche sans signe (StatDisplay.Stat).
public sealed record RemoteDisplay
{
    public string HpMode { get; init; } = "points";
    public string MpMode { get; init; } = "points";
    public bool ShowMp { get; init; } = true;
    public bool ShowShield { get; init; } = true;
    public string? HpLabel { get; init; }
    public string? MpLabel { get; init; }
    public bool StatTarget { get; init; }
}

// De quoi préremplir une demande de jet comme RollRequestWindow : seuil par défaut max(1, DiceMax / 2),
// indice selon LowerIsBetter. Le mode à seuils est dans RemoteDisplay.StatTarget.
public sealed record RemoteRules(int DiceMax, bool LowerIsBetter);

// L'action pleine de la barre de séance. Kind : « start » (op turnStart), « endTurn » (turnEnd, Name = celui
// dont c'est le tour) ou « nextRound » (turnNextRound).
public sealed record RemotePrimary(string Kind, string? Name = null);

// Id : StatValue.Id, celui qu'attendent les commandes roll et rollRequest.
public sealed record RemoteStat(string Id, string Name, int Mod);

// Color : « #rrggbb ».
public sealed record RemoteCounter(string Name, int Value, int Max, string Color);

public sealed record RemotePlayer
{
    public string Hash { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool Connected { get; init; }
    public bool IsGm { get; init; }
    public bool IsCoGm { get; init; }
    public int Hp { get; init; }
    public int HpMax { get; init; }
    public int Mp { get; init; }
    public int MpMax { get; init; }
    public int Shield { get; init; }
    public IReadOnlyList<RemoteStat> Stats { get; init; } = [];
    public IReadOnlyList<RemoteCounter> Counters { get; init; } = [];
    public int TempMod { get; init; }
    public int TempTurns { get; init; }
    public float MoveLeft { get; init; }
    public float MoveMax { get; init; }
    public string? Group { get; init; }
    public bool AwaitingRoll { get; init; }
}

// Id : « A » à « D », « 1 » à « 4 ». Attitude : « hostile », « neutral » ou « friendly ».
// Seuls les marqueurs porteurs de données (MarkerData.HasData) sont envoyés.
public sealed record RemoteMarker
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Attitude { get; init; } = "neutral";
    public bool Boss { get; init; }
    public bool Visible { get; init; }
    public int Hp { get; init; }
    public int HpMax { get; init; }
    public int Mp { get; init; }
    public int MpMax { get; init; }
    public int Shield { get; init; }
    public IReadOnlyList<RemoteStat> Stats { get; init; } = [];
    public IReadOnlyList<RemoteCounter> Counters { get; init; } = [];
    public int TempMod { get; init; }
    public int TempTurns { get; init; }
}

// Id : NetworkId au format « N ». Ni apparence, ni mods, ni position : rien qui serve hors du jeu.
public sealed record RemoteNpc
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool Creature { get; init; }
    public string Attitude { get; init; } = "neutral";
    public bool Boss { get; init; }
    public int Hp { get; init; }
    public int HpMax { get; init; }
    public int Shield { get; init; }
    public IReadOnlyList<RemoteStat> Stats { get; init; } = [];
    public IReadOnlyList<RemoteCounter> Counters { get; init; } = [];
    public int TempMod { get; init; }
}

// ActiveIndex : -1 quand personne n'a la main (combat préparé ou terminé). Encounter : numéro du combat,
// qui distingue deux combats au même round.
public sealed record RemoteTurn
{
    public bool Active { get; init; }
    public int Encounter { get; init; }
    public int Round { get; init; }
    public int ActiveIndex { get; init; } = -1;
    public int DiceMax { get; init; }
    public IReadOnlyList<RemoteTurnEntry> Entries { get; init; } = [];
}

// Kind : « player », « marker » ou « npc ». Ref : empreinte du joueur, lettre du marqueur ou Id du PNJ.
// HasActed tient compte du groupe (TurnState.HasEntryActed). GroupId (TurnGroup.Id) est la seule clé
// de regroupement : Group, le libellé, peut être vide ou partagé par deux groupes.
public sealed record RemoteTurnEntry
{
    public string Name { get; init; } = string.Empty;
    public string Kind { get; init; } = "player";
    public string? Ref { get; init; }
    public int Initiative { get; init; }
    public int Roll { get; init; }
    public int Mod { get; init; }
    public string? Stat { get; init; }
    public bool HasActed { get; init; }
    public string? GroupId { get; init; }
    public string? Group { get; init; }
}

// Grouped : les membres partagent un sous-groupe (GroupId), même sans lettre encore attribuée.
public sealed record RemotePendingGroup(string? Label, bool Grouped, IReadOnlyList<RemotePendingMember> Members);

public sealed record RemotePendingMember(string Name, string Hash);

// Les plus récents d'abord, 20 au plus. At : millisecondes depuis l'époque Unix.
public sealed record RemoteRoll
{
    public string Roller { get; init; } = string.Empty;
    public string? Stat { get; init; }
    public int Raw { get; init; }
    public int Mod { get; init; }
    public int Total { get; init; }
    public int DiceMax { get; init; }
    public int? Target { get; init; }
    public bool? Success { get; init; }
    public IReadOnlyList<int>? Dice { get; init; }
    public long At { get; init; }
}

// Weather : nom de la météo imposée, null pour celle de la zone. Time : « 14:00 », null pour l'heure du jeu.
public sealed record RemoteAmbiance
{
    public string? Weather { get; init; }
    public string? Time { get; init; }
}

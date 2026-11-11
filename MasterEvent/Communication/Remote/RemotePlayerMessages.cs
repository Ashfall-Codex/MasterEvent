using System.Collections.Generic;

namespace MasterEvent.Communication.Remote;

// Page joueur du second écran (player.html), servie par un second LocalGmServer : mêmes hello, welcome, ack
// et bye que la page MJ, mêmes conventions JSON (RemoteView.Json), aucune note. Elle montre ce que montre
// la vue joueur en jeu, jamais plus : ni stats d'autrui, ni marqueur caché, ni PNJ non répliqué, ni position.
// Commandes : RemoteOps.Self, dans cmd.lobby la valeur de state.room ; ce qui est permis est dans state.caps.

// Access : « player », ou « none » pour l'aperçu du MJ et sans joueur local ; tout le reste est alors vide.
// Room : LobbyCode en lobby, « party » sinon ; nulle avec l'accès « none ».
// View : « instance », « versionRejected », « joinRejected », « awaitingApproval », « offline », « reconnecting »,
// « outOfSession », « joining », « handover », « gmAway » ou « inSession », comme PlayerWindow.ResolveState.
// Request : en salle seulement (inSession, handover, gmAway). Announcement : en séance ou en reconnexion.
// Turn : combat montré, comme la vue (en salle, relais en séance, hors instance).
public sealed record RemotePlayerState
{
    public string Type => "state";
    public long Seq { get; init; }
    public string Access { get; init; } = "none";
    public IReadOnlyList<string> Caps { get; init; } = [];
    public string? Room { get; init; }
    public string? View { get; init; }
    // Un combat existe, même masqué par une coupure : la page bascule ses segments sur lui, pas sur Turn.
    public bool? CombatPresent { get; init; }
    public RemotePlayerViewDetail? ViewDetail { get; init; }
    public RemoteLink Link { get; init; } = new();
    public RemoteDisplay Display { get; init; } = new();
    public RemotePlayerGm? Gm { get; init; }
    public string? Template { get; init; }
    public bool LowerIsBetter { get; init; }
    public RemotePlayerPrimary? Primary { get; init; }
    public RemotePlayerRequest? Request { get; init; }
    public string? Announcement { get; init; }
    public RemoteAmbiance? Ambiance { get; init; }
    public RemotePlayerSheet? Me { get; init; }
    public IReadOnlyList<RemotePlayerField> Field { get; init; } = [];
    public IReadOnlyList<RemotePlayerMate> Players { get; init; } = [];
    public RemotePlayerTurn? Turn { get; init; }
    public IReadOnlyList<RemoteRoll> Rolls { get; init; } = [];
    public RemotePlayerRoll? LastRoll { get; init; }
}

// Rejection : « denied », « roomLimit », « rateLimited » ou « invalid », celui dont la vue affiche le texte.
// Consent : faux tant que l'accord sur les données partagées manque (message hors ligne).
public sealed record RemotePlayerViewDetail(string? Lobby, string? MinVersion, string? Rejection, bool Consent);

public sealed record RemotePlayerGm(string? Name, bool Connected);

// Kind : « roll » ou « endTurn ». StatName nul : jet sans stat. Hold : « none », « sent », « handover » ou
// « gmAway » ; hors « none », la fin de tour est grisée.
public sealed record RemotePlayerPrimary(string Kind, string? StatName, bool CanEndTurn, bool GroupTurn, string Hold);

// StatId nul : jet sans stat, ou stat absente de la fiche (Missing).
public sealed record RemotePlayerRequest(string? StatId, string StatName, int Threshold, bool Missing);

// Shield vaut 0 quand le modèle masque le bouclier, ici comme dans les autres listes.
public sealed record RemotePlayerSheet
{
    public string Name { get; init; } = string.Empty;
    public string Hash { get; init; } = string.Empty;
    public string? Sheet { get; init; }
    public int Hp { get; init; }
    public int HpMax { get; init; }
    public int Mp { get; init; }
    public int MpMax { get; init; }
    public int Shield { get; init; }
    public IReadOnlyList<RemoteCounter> Counters { get; init; } = [];
    public int TempModifier { get; init; }
    public int TempRounds { get; init; }
    public float MoveLeft { get; init; }
    public float MoveMax { get; init; }
    public IReadOnlyList<RemoteStat> Stats { get; init; } = [];
    public bool MyTurn { get; init; }
}

// Kind : « marker » (Ref : « A » à « D », « 1 » à « 4 ») ou « npc » (Ref : NetworkId au format « N »).
// Les champs facultatifs n'existent que pour un marqueur, comme son détail en jeu ; Mp et MpMax seulement
// si le modèle montre l'éther, LastRoll seulement après un jet.
public sealed record RemotePlayerField
{
    public string Kind { get; init; } = "marker";
    public string Ref { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool Creature { get; init; }
    public string Attitude { get; init; } = "neutral";
    public int Hp { get; init; }
    public int HpMax { get; init; }
    public int Shield { get; init; }
    public bool Boss { get; init; }
    public int? Mp { get; init; }
    public int? MpMax { get; init; }
    public IReadOnlyList<RemoteCounter>? Counters { get; init; }
    public int? TempModifier { get; init; }
    public int? TempRounds { get; init; }
    public int? LastRoll { get; init; }
    public int? LastRollMax { get; init; }
}

public sealed record RemotePlayerMate
{
    public string Name { get; init; } = string.Empty;
    public string Hash { get; init; } = string.Empty;
    public bool Connected { get; init; }
    public int Hp { get; init; }
    public int HpMax { get; init; }
    public int Shield { get; init; }
    public bool CoGm { get; init; }
}

// ActiveIndex : -1 quand tout le monde a joué. TurnOf : nul dans ce cas.
public sealed record RemotePlayerTurn
{
    public int Round { get; init; }
    public int Acted { get; init; }
    public int Total { get; init; }
    public int DiceMax { get; init; }
    public int ActiveIndex { get; init; } = -1;
    public string? TurnOf { get; init; }
    public IReadOnlyList<RemotePlayerTurnEntry> Entries { get; init; } = [];
    public RemotePlayerAlsoOnField AlsoOnField { get; init; } = new([], []);
}

// Kind : « player », « marker », « npc » ou « other ». Ref : empreinte du joueur, lettre du marqueur montré
// ou Id du PNJ présent dans field, nulle sinon (ligne sans barre) ; les PV se lisent dans field, players ou me.
// Roll, Mod et Stat : l'infobulle de l'initiative (TurnText.InitiativeTooltip), rien quand Roll vaut 0.
public sealed record RemotePlayerTurnEntry
{
    public string Name { get; init; } = string.Empty;
    public int Initiative { get; init; }
    public int Roll { get; init; }
    public int Mod { get; init; }
    public string? Stat { get; init; }
    public bool Acted { get; init; }
    public bool Active { get; init; }
    public bool Mine { get; init; }
    public string Kind { get; init; } = "other";
    public string? Ref { get; init; }
    public string? Group { get; init; }
    public string? GroupLabel { get; init; }
}

// Field : Ref des entrées de field absentes de l'ordre ; Players : empreintes des joueurs absents de l'ordre.
public sealed record RemotePlayerAlsoOnField(IReadOnlyList<string> Field, IReadOnlyList<string> Players);

// Champs de RemoteRoll, plus le détail du dernier jet de Ma fiche : StatMod, part de la stat (nulle sans
// stat), et TempMod, bonus temporaire (nul sans bonus). Mod reste leur somme.
public sealed record RemotePlayerRoll
{
    public string Roller { get; init; } = string.Empty;
    public string? Stat { get; init; }
    public int Raw { get; init; }
    public int Mod { get; init; }
    public int? StatMod { get; init; }
    public int? TempMod { get; init; }
    public int Total { get; init; }
    public int DiceMax { get; init; }
    public int? Target { get; init; }
    public bool? Success { get; init; }
    public IReadOnlyList<int>? Dice { get; init; }
    public long At { get; init; }
}

<p align="center">
  <img src="https://repo.ashfall-codex.dev/img/masterevent.png" alt="MasterEvent" width="128" />
</p>

<h1 align="center">MasterEvent</h1>

<p align="center">
  <b>Assistant pour FFXIV</b> : Outil pour les Ma&#xEE;tres du Jeu et les joueurs en roleplay, permettant de g&#xE9;rer des marqueurs de terrain, jets de d&#xE9;s, initiative, fiches de personnages, m&#xE9;t&#xE9;o et bien plus, avec synchronisation en temps r&#xE9;el.
</p>

---

## Fonctionnalit&#xE9;s

### Assistant de configuration

- **Setup guid&#xE9;** au premier lancement, style Apple Setup Assistant
- **7 &#xE9;tapes** : Bienvenue, RGPD, Mod&#xE8;le, R&#xE9;sultat, Fiche de personnage, Test de d&#xE9;s, Compl&#xE9;tion
- **Cr&#xE9;ation de mod&#xE8;le int&#xE9;gr&#xE9;e** avec &#xE9;diteur complet ou **import par code de partage**
- **Cr&#xE9;ation de fiche** avec pr&#xE9;-remplissage du nom du personnage actuel
- **Test interactif** des d&#xE9;s directement dans l&#x27;assistant
- Accessible &#xE0; tout moment depuis les R&#xE9;glages &gt; Guide

### Gestion des marqueurs

- **8 marqueurs** (A, B, C, D, 1, 2, 3, 4) enti&#xE8;rement configurables
- **Nom** personnalis&#xE9; (max 26 caract&#xE8;res)
- **Points de vie** (PV) avec barre visuelle, mode pourcentage ou points
- **Points d&#x27;&#xE9;ther** (PE) optionnels avec barre d&#xE9;di&#xE9;e
- **Bouclier** avec overlay visuel sur la barre de vie
- **Attitude** : Hostile (rouge), Neutre (jaune), Amical (vert)
- **Statut Boss** pour les ennemis importants
- **Compteurs personnalis&#xE9;s** illimit&#xE9;s avec couleur RGB configurable
- **Statistiques** configurables par marqueur (MJ uniquement, non visibles par les joueurs)
- **Bonus/malus temporaire** applicable aux marqueurs et joueurs
- **Placement, d&#xE9;placement et suppression** des waymarks en jeu

### Syst&#xE8;me de d&#xE9;s

- **Multi-d&#xE9;s** : support complet des formules XdY (ex: `2d20`, `5d6`)
- **Animation multi-d&#xE9;s** : N icosa&#xE8;dres 3D anim&#xE9;s c&#xF4;te &#xE0; c&#xF4;te avec r&#xE9;v&#xE9;lation individuelle
- **&#xC9;diteur de formule** : deux champs &#xAB; Nombre de d&#xE9;s &#xBB; et &#xAB; Nombre de faces &#xBB; (au lieu d&#x27;un champ texte)
- **Jets avec statistiques** : modificateurs de stat appliqu&#xE9;s automatiquement
- **Bonus/malus temporaires** pris en compte dans les jets
- **Breakdown** affich&#xE9; en chat et historique : `14 + 13 = 27/40 (+5) = 32`
- **Historique des jets** consultable (20 derniers) avec effacement
- Diffusion en temps r&#xE9;el &#xE0; tous les joueurs connect&#xE9;s
- R&#xE9;trocompatible avec les anciens clients (champ `rollDice` nullable)

### Syst&#xE8;me de mod&#xE8;les (templates)

- Cr&#xE9;ation de mod&#xE8;les d&#x27;&#xE9;v&#xE9;nement personnalis&#xE9;s
- Configuration par mod&#xE8;le : mode PV/PE, bouclier, barre PE, formule de d&#xE9;, stat d&#x27;initiative, compteurs, statistiques
- **Export/import** de mod&#xE8;les via code court (6 caract&#xE8;res) sur le serveur relais
- Option de stockage **permanent** ou **temporaire** (7 jours) sur le serveur
- **Listing des mod&#xE8;les partag&#xE9;s** avec code, type (permanent/temporaire) et bouton copier
- Protection contre le double partage (bouton gris&#xE9; si d&#xE9;j&#xE0; partag&#xE9;)
- **Partage au groupe** : diffusion du mod&#xE8;le actif &#xE0; tous les joueurs connect&#xE9;s
- Biblioth&#xE8;que de mod&#xE8;les sauvegard&#xE9;e localement
- Mod&#xE8;le par d&#xE9;faut configurable

### Syst&#xE8;me de profils (fiches personnage)

- **Cr&#xE9;ation de profils** li&#xE9;s &#xE0; un mod&#xE8;le import&#xE9;
- Personnalisation des PV, PE, statistiques et compteurs par profil
- **Plusieurs profils** possibles (un par &#xE9;v&#xE9;nement / mod&#xE8;le)
- S&#xE9;lection de profil dans la **vue joueur** (filtr&#xE9; par le mod&#xE8;le actif du MJ)
- Sauvegarde locale en JSON

### Vue joueur

- **Sidebar avec deux onglets** : vue d&#x27;ensemble et jets de d&#xE9;s
- **Carte joueur** : PV, PE, compteurs, statistiques en lecture seule
- **Grille de jets** : un bouton par stat pour lancer directement avec le bon modificateur
- **Historique des jets** int&#xE9;gr&#xE9; avec breakdown multi-d&#xE9;s
- **S&#xE9;lection de fiche** : liste d&#xE9;roulante filtr&#xE9;e par le mod&#xE8;le actif
- Accessible via `/masterevent joueur` ou bouton dans les param&#xE8;tres

### Mode MJ + Joueur

- **Participer en tant que joueur** : le MJ peut cocher cette option pour ouvrir automatiquement la vue joueur en parall&#xE8;le de la vue MJ
- Permet au MJ de lancer ses propres d&#xE9;s et g&#xE9;rer sa fiche tout en ma&#xEE;trisant la session

### Suivi des tours / Initiative

- **Lancement de combat** avec jet d&#x27;initiative automatique pour tous les marqueurs et joueurs
- **Ajout de participants en cours de combat** via le bouton &#xAB; + &#xBB; (marqueurs ou joueurs non encore pr&#xE9;sents)
- **Ordre de passage** tri&#xE9; par initiative, avec indicateur du tour actif
- **Actions** : cocher &#xAB; a jou&#xE9; &#xBB;, relancer l&#x27;initiative, monter/descendre, retirer
- **Tour suivant** : remet les coches &#xE0; z&#xE9;ro, d&#xE9;cr&#xE9;mente les bonus/malus temporaires
- **Notifications toast** pour annoncer le prochain participant et la fin de tour
- Synchronisation en temps r&#xE9;el avec tous les joueurs connect&#xE9;s

### Gestion du groupe

- **Vue MJ** (Ma&#xEE;tre du Jeu) pour le chef de groupe
- **Vue Joueur** en lecture seule pour les autres membres
- **Syst&#xE8;me de co-MJ** : promotion/r&#xE9;trogradation de joueurs
- Suivi des PV/PE individuels des joueurs
- **Bonus/malus temporaire** par joueur (MJ uniquement)
- Indicateur de connexion en temps r&#xE9;el par joueur
- **Mode Raid Alliance** : g&#xE9;n&#xE9;ration d&#x27;un code de salle 6 caract&#xE8;res pour connecter jusqu&#x27;&#xE0; 24 joueurs (3 groupes de 8) sur la m&#xEA;me session, ind&#xE9;pendamment du groupe FFXIV local
- **Indicateurs visuels par groupe** : badge color&#xE9; `[A]`, `[B]`, `[C]`&#x2026; et compteur par groupe
- **Persistance du code alliance** : survit aux reloads/crashes, auto-rejoin &#xE0; la reconnexion
- **Kick de joueur** : retrait de joueurs individuels de l&#x27;alliance avec notification

### Synchronisation multijoueur

- Communication en temps r&#xE9;el via WebSocket (WSS/TLS)
- Serveur relais d&#xE9;di&#xE9; en Rust avec gestion de salles par groupe
- **Mode Alliance** : salles par code (ind&#xE9;pendant du groupe FFXIV), tracking automatique des joueurs des autres groupes, identification par groupe d&#x27;origine
- **Reconnexion automatique** avec backoff exponentiel (1s &#xE0; 30s)
- **R&#xE9;cup&#xE9;ration de session** : cache serveur + cache local en cas de crash
- Notifications de connexion/d&#xE9;connexion en chat
- **API REST** pour l&#x27;export/import de mod&#xE8;les (`POST/GET /api/templates`)

### M&#xE9;t&#xE9;o et heure &#xE9;orz&#xE9;enne

- **Contr&#xF4;le de la m&#xE9;t&#xE9;o** : changement du temps affich&#xE9; en jeu (d&#xE9;gag&#xE9;, pluie, orage, brouillard&#x2026;)
- **Contr&#xF4;le de l&#x27;heure** : gel de l&#x27;heure &#xE9;orz&#xE9;enne &#xE0; une valeur choisie (0h&#x2013;23h)
- **Enti&#xE8;rement autonome** : aucune d&#xE9;pendance externe (ni Weatherman, ni Brio). Hook direct sur les fonctions du jeu + patch m&#xE9;moire sur le rendu
- **Listes de m&#xE9;t&#xE9;o par zone** charg&#xE9;es depuis les donn&#xE9;es Lumina du jeu
- **Synchronisation** : m&#xE9;t&#xE9;o et heure diffus&#xE9;es &#xE0; tous les joueurs connect&#xE9;s via le relay
- **R&#xE9;initialisation** : retour instantan&#xE9; &#xE0; la m&#xE9;t&#xE9;o et l&#x27;heure normales du jeu

### Presets

- Sauvegarde de l&#x27;&#xE9;tat complet des marqueurs en preset nomm&#xE9;
- Chargement et suppression de presets
- Stockage local en JSON

### Localisation

- **Fran&#xE7;ais** (langue par d&#xE9;faut)
- **English**
- Changement de langue &#xE0; chaud depuis les param&#xE8;tres

### Conformit&#xE9; RGPD

- **Consentement int&#xE9;gr&#xE9;** dans l&#x27;assistant de configuration au premier lancement
- Consentement versionn&#xE9; (v2) et r&#xE9;vocable depuis les r&#xE9;glages
- Donn&#xE9;es de session supprim&#xE9;es &#xE0; la d&#xE9;connexion ; seuls les mod&#xE8;les partag&#xE9;s en permanence sont conserv&#xE9;s sur le serveur
- Journalisation anonymis&#xE9;e (hash SHA-256 uniquement)
- Information compl&#xE8;te sur les droits (acc&#xE8;s, effacement, opposition)
- Donn&#xE9;es transmises : nom de personnage, identifiant de groupe, donn&#xE9;es des marqueurs (PV, PE, attitude, bouclier), fiches de personnage, jets de d&#xE9;s, mod&#xE8;les, param&#xE8;tres de m&#xE9;t&#xE9;o, identifiant anonymis&#xE9;

## Architecture

Le projet est compos&#xE9; de deux parties :

| Composant | Technologie | Description |
|---|---|---|
| `MasterEvent/` | C# / .NET 10 / Dalamud SDK | Plugin FFXIV (tourne dans le jeu) |
| `MasterEventRelay/` | Rust / Axum / SQLite | Serveur relais de synchronisation |

### Plugin (C#)

- **Point d&#x27;entr&#xE9;e** : `Plugin.cs` &#x2014; enregistre la commande `/masterevent`, les hooks UI et le tick framework
- **R&#xF4;les** : Chef de groupe = MJ, autres = Joueurs. Mode solo = MJ local
- **Communication** : Messages JSON via WebSocket, thread-safe avec `ConcurrentQueue`
- **UI** : ImGui avec th&#xE8;me rouge/sombre, fen&#xEA;tres MJ et Joueur s&#xE9;par&#xE9;es, assistant de configuration d&#xE9;di&#xE9;
- **Mod&#xE8;les** : `EventTemplate` (d&#xE9;finition d&#x27;&#xE9;v&#xE9;nement), `PlayerSheet` (fiche personnage), `StatDefinition` / `StatValue` (statistiques), `SharedTemplate` (mod&#xE8;les partag&#xE9;s)
- **Persistance** : Config Dalamud, presets/mod&#xE8;les/fiches/partages en JSON local

### Serveur relais (Rust)

- **Axum** + **Tokio** pour les WebSocket et HTTP asynchrones
- **SQLite** (rusqlite) pour le stockage persistant des mod&#xE8;les
- Salles par `partyId`, expiration apr&#xE8;s inactivit&#xE9; configurable
- Cache d&#x27;&#xE9;tat pour r&#xE9;cup&#xE9;ration de session
- **Stockage de mod&#xE8;les** avec codes courts et option permanente
- Nettoyage automatique des rooms (5 min) et mod&#xE8;les expir&#xE9;s (1h)
- Rate limiting (30 msg/s par client)
- Endpoint `/health` pour monitoring
- TLS via reverse proxy (Caddy)

## Build

### Plugin
```bash
dotnet build MasterEvent/MasterEvent.csproj
```
N&#xE9;cessite .NET 10.x SDK et Dalamud (via XIV on Mac ou &#xE9;quivalent).

### Serveur relais
```bash
cd MasterEventRelay
cargo build --release
./target/release/master-event-relay
```
Copier `.env.example` en `.env` pour la configuration (PORT, HOST, ROOM_EXPIRY_MS, LOG_LEVEL, DATABASE_PATH).

## Commandes

| Commande | Description |
|---|---|
| `/masterevent` | Ouvre la fen&#xEA;tre principale (MJ ou joueur selon le r&#xF4;le) |
| `/masterevent joueur` | Ouvre/ferme la vue joueur |
| `/masterevent config` | Ouvre les param&#xE8;tres |
| `/masterevent help` | Affiche l&#x27;aide |
| `/me` | Alias de `/masterevent` |

### Commandes debug (mode debug activ&#xE9;)

| Commande | Description |
|---|---|
| `/masterevent connect` | Connexion manuelle au relais |
| `/masterevent disconnect` | D&#xE9;connexion du relais |
| `/masterevent mj` | Basculer en vue MJ |

## Licence

Voir le fichier [LICENSE](LICENSE).

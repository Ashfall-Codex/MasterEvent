<p align="center">
  <img src="https://repo.umbra-sync.net/images/MasterEvent.png" alt="MasterEvent" width="128" />
</p>

<h1 align="center">MasterEvent</h1>

<p align="center">
  <b>Plugin Dalamud pour FFXIV</b> : Outil de Ma&#xEE;tre de jeu pour le roleplay, permettant d&#x27;assigner des noms, points de vie, attitudes et statuts aux marqueurs de terrain (waymarks), avec synchronisation en temps r&#xE9;el vers tous les membres du groupe.
</p>

---

## Fonctionnalit&#xE9;s

### Gestion des marqueurs

- **8 marqueurs** (A, B, C, D, 1, 2, 3, 4) enti&#xE8;rement configurables
- **Nom** personnalis&#xE9; (max 26 caract&#xE8;res)
- **Points de vie** (HP) avec barre visuelle, mode pourcentage ou points
- **Points d&#x27;&#xE9;ther** (MP) optionnels avec barre d&#xE9;di&#xE9;e
- **Bouclier** avec overlay visuel sur la barre de vie
- **Attitude** : Hostile (rouge), Neutre (jaune), Amical (vert)
- **Statut Boss** pour les ennemis importants
- **Compteurs personnalis&#xE9;s** illimit&#xE9;s avec couleur RGB configurable
- **Placement, d&#xE9;placement et suppression** des waymarks en jeu

### Syst&#xE8;me de d&#xE9;s

- Lancer de d&#xE9;s configurable (1 &#xE0; 1000)
- R&#xE9;sultat affich&#xE9; sur la carte du marqueur concern&#xE9;
- Diffusion en temps r&#xE9;el &#xE0; tous les joueurs connect&#xE9;s

### Syst&#xE8;me de templates

- Cr&#xE9;ation de mod&#xE8;les d&#x27;&#xE9;v&#xE9;nement personnalis&#xE9;s
- Configuration par template : mode PV/PE, bouclier, barre MP, d&#xE9;s, compteurs
- **Partage de template** &#xE0; tous les joueurs du groupe
- Biblioth&#xE8;que de templates sauvegard&#xE9;e localement
- Template par d&#xE9;faut configurable

### Gestion du groupe

- **Vue MJ** (Ma&#xEE;tre du Jeu) pour le chef de groupe
- **Vue Joueur** en lecture seule pour les autres membres
- **Syst&#xE8;me de co-MJ** : promotion/r&#xE9;trogradation de joueurs
- Suivi des HP individuels des joueurs
- Indicateur de connexion en temps r&#xE9;el par joueur

### Synchronisation multijoueur

- Communication en temps r&#xE9;el via WebSocket
- Serveur relais d&#xE9;di&#xE9; avec gestion de salles par groupe
- **Reconnexion automatique** avec backoff exponentiel (1s &#xE0; 30s)
- **R&#xE9;cup&#xE9;ration de session** : cache serveur + cache local en cas de crash
- Notifications de connexion/d&#xE9;connexion en chat

### Presets

- Sauvegarde de l&#x27;&#xE9;tat complet des marqueurs en preset nomm&#xE9;
- Chargement et suppression de presets
- Stockage local en JSON

### Localisation

- **Fran&#xE7;ais** (langue par d&#xE9;faut)
- **English**
- Changement de langue &#xE0; chaud depuis les param&#xE8;tres

### Conformit&#xE9; RGPD

- Fen&#xEA;tre de consentement au premier lancement
- Consentement versionn&#xE9; et r&#xE9;vocable
- Aucune donn&#xE9;e personnelle stock&#xE9;e de mani&#xE8;re persistante sur le serveur
- Journalisation anonymis&#xE9;e (hash SHA-256 uniquement)
- Information compl&#xE8;te sur les droits (acc&#xE8;s, effacement, opposition)

## Architecture

Le projet est compos&#xE9; de deux parties :

| Composant | Technologie | Description |
|---|---|---|
| `MasterEvent/` | C# / .NET 10 / Dalamud SDK | Plugin FFXIV (tourne dans le jeu) |
| `MasterEventRelay/` | Node.js / WebSocket | Serveur relais de synchronisation |

### Plugin (C#)

- **Point d&#x27;entr&#xE9;e** : `Plugin.cs` &#x2014; enregistre la commande `/masterevent`, les hooks UI et le tick framework
- **R&#xF4;les** : Chef de groupe = MJ, autres = Joueurs. Mode solo = MJ local
- **Communication** : Messages JSON via WebSocket, thread-safe avec `ConcurrentQueue`
- **UI** : ImGui avec th&#xE8;me rouge/sombre, fen&#xEA;tres MJ et Joueur s&#xE9;par&#xE9;es

### Serveur relais (Node.js)

- Salles par `partyId`, expiration apr&#xE8;s inactivit&#xE9; configurable
- Cache d&#x27;&#xE9;tat pour r&#xE9;cup&#xE9;ration de session
- Endpoint `/health` pour monitoring

## Build

### Plugin
```bash
dotnet build MasterEvent/MasterEvent.csproj
```
N&#xE9;cessite .NET 10.x SDK et Dalamud (via XIV on Mac ou &#xE9;quivalent).

### Serveur relais
```bash
cd MasterEventRelay && npm install
node server.js
```
Copier `.env.example` en `.env` pour la configuration (PORT, HOST, ROOM_EXPIRY_MS, LOG_LEVEL).

## Commandes

| Commande | Description |
|---|---|
| `/masterevent` | Ouvre la fen&#xEA;tre principale |
| `/masterevent config` | Ouvre les param&#xE8;tres |
| `/masterevent help` | Affiche l&#x27;aide |
| `/me` | Alias de `/masterevent` |

### Commandes debug (mode debug activ&#xE9;)

| Commande | Description |
|---|---|
| `/masterevent connect` | Connexion manuelle au relais |
| `/masterevent disconnect` | D&#xE9;connexion du relais |
| `/masterevent joueur` | Basculer en vue joueur |
| `/masterevent mj` | Basculer en vue MJ |

## Licence

Voir le fichier [LICENSE](LICENSE).

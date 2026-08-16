<p align="center">
  <img src="https://repo.ashfall-codex.dev/img/masterevent.png" alt="MasterEvent" width="128" />
</p>

<h1 align="center">MasterEvent</h1>

<p align="center">
  <b>Assistant pour FFXIV</b> : Outil pour les Maîtres du Jeu et les joueurs en roleplay, permettant de gérer des marqueurs de terrain, jets de dés, initiative, fiches de personnages, météo et bien plus, avec synchronisation en temps réel.
</p>

<p align="center">
<a href="https://argus.arediss.fr"><img src="https://argus.arediss.fr/api/projects/masterevent/badge.svg" alt="Quality gate" /></a>
<a href="https://argus.arediss.fr"><img src="https://argus.arediss.fr/api/projects/masterevent/badge.svg?metric=reliability" alt="Fiabilité" /></a>
<a href="https://argus.arediss.fr"><img src="https://argus.arediss.fr/api/projects/masterevent/badge.svg?metric=security" alt="Sécurité" /></a>
<a href="https://argus.arediss.fr"><img src="https://argus.arediss.fr/api/projects/masterevent/badge.svg?metric=maintainability" alt="Maintenabilité" /></a>
<a href="https://argus.arediss.fr"><img src="https://argus.arediss.fr/api/projects/masterevent/badge.svg?metric=vulnerabilities" alt="Vulnérabilités" /></a></p>

---

## Fonctionnalités

### Assistant de configuration

- **Setup guidé** au premier lancement.
- **7 étapes** : Bienvenue, RGPD, Modèle, Résultat, Fiche de personnage, Test de dés, Complétion
- **Création de modèle intégrée** avec éditeur complet ou **import par code de partage**
- **Création de fiche** avec pré-remplissage du nom du personnage actuel
- **Test interactif** des dés directement dans l'assistant
- Accessible à tout moment depuis les Réglages > Guide

### Gestion des marqueurs

- **8 marqueurs** (A, B, C, D, 1, 2, 3, 4) entièrement configurables
- **Nom** personnalisé (max 26 caractères)
- **Points de vie** (PV) avec barre visuelle, mode pourcentage ou points
- **Points d'éther** (PE) optionnels avec barre dédiée
- **Bouclier** avec overlay visuel sur la barre de vie
- **Attitude** : Hostile (rouge), Neutre (jaune), Amical (vert)
- **Statut Boss** pour les ennemis importants
- **Compteurs personnalisés** illimités avec couleur RGB configurable
- **Statistiques** configurables par marqueur (MJ uniquement, non visibles par les joueurs)
- **Bonus/malus temporaire** applicable aux marqueurs et joueurs
- **Placement, déplacement et suppression** des waymarks en jeu

### PNJ incarnés

- **Pose de PNJ natifs** dans le monde, **8 simultanés maximum**, sans aucun plugin tiers
- **Trois sources d'apparence** : modèle par défaut, copie du personnage local, ou import d'un fichier Anamnesis `.chara`
- **Emotes et postures** : catalogue d'emotes issu des données du jeu, maintien de la pose, dégainement des armes, rejeu de l'animation
- **Fiche complète** par PNJ : PV, bouclier, attitude, statut Boss, compteurs personnalisés et statistiques, au même titre qu'un marqueur
- **Alignement sur le modèle actif** : un PNJ posé reçoit les stats et compteurs du modèle ; les PNJ déjà en place se réalignent à chaque activation de modèle, en conservant les valeurs déjà saisies
- **Jets de dés par PNJ**, avec ou sans statistique, bonus/malus temporaire pris en compte
- **Initiative** : les PNJ rejoignent l'ordre de passage avec le modificateur de la stat d'initiative du modèle
- **Presets de PNJ** : apparence, posture et fiche enregistrées sous un nom, pour reposer un personnage récurrent
- **Synchronisation dans la session** : les PNJ du MJ apparaissent chez les joueurs présents dans la même zone (position, rotation, emote, vitalité)
- **Garde de sécurité** : spawn refusé en donjon matchmaké, en PvP, pendant une cinématique et lors des transitions de zone

### Mode tactique

- **Vue tour par tour** dédiée, en complément du suivi d'initiative
- **Overlay de terrain** : barres de vie flottantes au-dessus des participants, joueurs et PNJ, avec code couleur par nature
- **Caméra tactique** : vue plongeante optionnelle pendant le combat, état initial restauré en sortie
- **Quota de déplacement** : suivi en yalms du déplacement pendant le tour, tracé du chemin au sol, quota défini par modèle ou par statistique de déplacement
- **« Terminer mon tour »** : le joueur signale la fin de son tour, le MJ reste seul maître de l'ordre de passage
- **Masquage des plaques de nom** pendant le combat (option)
- **Emote de mise à terre** automatique au passage des PV à zéro (option)
- Overlays affichés uniquement en jeu, jamais sur l'écran d'accueil

### Bloc-notes

- **Notes libres de session**, jusqu'à 20 000 caractères, dans une fenêtre dédiée
- **Sauvegarde automatique** deux secondes après la dernière frappe
- Synchronisation vers le coffre Ashfall Connect au même titre que les fiches et les modèles

### Système de dés

- **Multi-dés** : support complet des formules XdY (ex : `2d20`, `5d6`)
- **Animation multi-dés** : N icosaèdres 3D animés côte à côte avec révélation individuelle
- **Vitesse d'animation réglable** : x1, x1.5 ou x2 depuis les paramètres
- **Éditeur de formule** : deux champs « Nombre de dés » et « Nombre de faces » (au lieu d'un champ texte)
- **Jets avec statistiques** : modificateurs de stat appliqués automatiquement
- **Bonus/malus temporaires** pris en compte dans les jets
- **Seuils critiques configurables par modèle** : réussite et échec critique selon un seuil personnalisé, avec deux modes (« plus c'est haut, mieux c'est » ou « plus c'est bas, mieux c'est ») pour couvrir les systèmes de jeu roll-under
- **Breakdown** affiché en chat et historique : `14 + 13 = 27/40 (+5) = 32`
- **Historique des jets** consultable (20 derniers) avec effacement
- Diffusion en temps réel à tous les joueurs connectés
- Rétrocompatible avec les anciens clients (champ `rollDice` nullable)

### Système de modèles (templates)

- Création de modèles d'événement personnalisés
- Configuration par modèle : mode PV/PE, bouclier, barre PE, formule de dé, seuils critiques, stat d'initiative, stat de déplacement, quota de déplacement, compteurs, statistiques
- **Deux modes de résolution des statistiques** : modificateur ajouté au jet, ou seuil à atteindre
- **Partage de modèles** via code court (6 caractères) sur le serveur relais
- Option de stockage **permanent** ou **temporaire** (7 jours) sur le serveur
- **Versioning et abonnements** :
  - Chaque modèle partagé porte un numéro de version qui s'incrémente à chaque mise à jour
  - Les joueurs qui importent un modèle deviennent **abonnés** : le modèle est en lecture seule chez eux (édition et partage désactivés)
  - **Mise à jour automatique** : au démarrage ou via notification temps réel, le plugin compare les versions et télécharge la dernière itération publiée par le créateur
  - **Seul le créateur** peut publier une mise à jour, grâce à un jeton d'autorisation stocké localement (vérification par hash côté serveur)
- **Listing séparé** : « Mes modèles » (créés localement, tous droits) et « Modèles abonnés » (importés, cadenas visuel, boutons limités à Charger / Se désabonner)
- **Synchronisation des fiches liées** : quand un modèle abonné est mis à jour, les `PlayerSheet` qui l'utilisent sont alignées automatiquement (stats et compteurs ajoutés, retirés ou renommés selon les identifiants stables du modèle). Un résumé par fiche est affiché dans le chat.
- **Partage au groupe** : diffusion du modèle actif à tous les joueurs connectés dans la session courante
- Bibliothèque de modèles sauvegardée localement, modèle par défaut configurable

### Système de profils (fiches personnage)

- **Création de profils** liés à un modèle importé ou créé
- Personnalisation des PV, PE, statistiques et compteurs par profil
- **Plusieurs profils** possibles (un par événement / modèle)
- **Adaptation automatique** lors de la mise à jour du modèle parent (cf. plus haut)
- Sélection de profil dans la **vue joueur** (filtré par le modèle actif du MJ)
- Sauvegarde locale en JSON

### Vue joueur

- **Sidebar avec deux onglets** : vue d'ensemble et jets de dés
- **Carte joueur** : PV, PE, compteurs, statistiques en lecture seule
- **Grille de jets** : un bouton par stat pour lancer directement avec le bon modificateur
- **Historique des jets** intégré avec breakdown multi-dés
- **Sélection de fiche** : liste déroulante filtrée par le modèle actif
- Accessible via `/masterevent joueur` ou bouton dans les paramètres

### Mode MJ + Joueur

- **Participer en tant que joueur** : le MJ peut cocher cette option pour ouvrir automatiquement la vue joueur en parallèle de la vue MJ
- Permet au MJ de lancer ses propres dés et gérer sa fiche tout en maîtrisant la session

### Suivi des tours / Initiative

- **Lancement de combat** avec jet d'initiative automatique pour tous les marqueurs et joueurs
- **Ajout de participants en cours de combat** via le bouton « + » (marqueurs ou joueurs non encore présents)
- **Ordre de passage** trié par initiative, avec indicateur du tour actif
- **Groupes de passage** : plusieurs participants peuvent être fusionnés dans une même phase de tour et jouer simultanément (« Alice, Bob et Charlie jouent »), avec un libellé personnalisable par groupe et un état « a joué » partagé
- **Actions par entrée** : cocher « a joué », relancer l'initiative, monter/descendre dans l'ordre, fusionner avec un voisin ou détacher, retirer du combat
- **Actions par groupe** : déplacer tout le bloc (montée/descente), renommer, dissoudre
- **Tour suivant** : remet les coches à zéro, décrémente les bonus/malus temporaires
- **Notifications toast** pour annoncer le prochain participant (ou le groupe) et la fin de tour
- Synchronisation en temps réel avec tous les joueurs connectés

### Annonce du MJ

- **Message libre** à l'écran (overlay rouge rubis centré) et dans le chat, diffusé à tous les joueurs connectés
- Limité à 180 caractères, wrap automatique multi-lignes, durée d'affichage proportionnelle à la longueur du message (3 à 10 secondes)
- Déclenché depuis la sidebar via un bouton mégaphone dédié (réservé au MJ)

### Gestion du groupe

- **Vue MJ** (Maître du Jeu) pour le chef de groupe
- **Vue Joueur** en lecture seule pour les autres membres
- **Système de co-MJ** : promotion/rétrogradation de joueurs
- Suivi des PV/PE individuels des joueurs
- **Bonus/malus temporaire** par joueur (MJ uniquement)
- Indicateur de connexion en temps réel par joueur
- **Mode Raid Alliance** : génération d'un code de salle 6 caractères pour connecter jusqu'à 24 joueurs (3 groupes de 8) sur la même session, indépendamment du groupe FFXIV local
- **Indicateurs visuels par groupe** : badge coloré `[A]`, `[B]`, `[C]`… et compteur par groupe
- **Persistance du code alliance** : survit aux reloads/crashes, auto-rejoin à la reconnexion
- **Kick de joueur** : retrait de joueurs individuels de l'alliance avec notification

### Synchronisation multijoueur

- Communication en temps réel via WebSocket (WSS/TLS)
- Serveur relais dédié en Rust avec gestion de salles par groupe
- **Authentification du MJ** : un jeton local unique est généré à l'installation et vérifié côté serveur (hash SHA-256) pour empêcher qu'un étranger prenne le contrôle d'une session en connaissant simplement son identifiant
- **CORS restrictif** : les requêtes HTTP ne sont acceptées que depuis les origines configurées (`ALLOWED_ORIGINS`) ; les clients natifs (plugin Dalamud) passent toujours
- **Limitation par IP** : maximum 10 connexions WebSocket par minute et 5 créations de salles par heure et par adresse IP, avec un plafond global de salles simultanées
- **Mode Alliance** : salles par code (indépendant du groupe FFXIV), tracking automatique des joueurs des autres groupes, identification par groupe d'origine
- **Lobby avec file d'admission** (protocole 2) : un joueur extérieur au groupe demande à entrer, le MJ voit la demande et l'approuve ou la refuse ; approuver un chef de groupe fait entrer tous ses coéquipiers sans nouvelle demande
- **Index party vers lobby** : un membre resté dans la salle de sa party est redirigé vers le lobby rejoint par son chef, sans avoir à connaître le code
- **Cohabitation de versions** : les clients 1.4.x continuent de fonctionner selon l'ancien protocole, le plancher étant fixé par `MIN_VERSION` côté relais
- **Distinction des déconnexions** volontaires et brutales, avec notification adaptée
- **Reconnexion automatique** avec backoff exponentiel (1s à 30s)
- **Récupération de session** : cache serveur + cache local en cas de crash
- **Shutdown gracieux** : à l'arrêt du serveur, les clients connectés reçoivent une frame Close propre avant coupure
- Notifications de connexion/déconnexion en chat
- **API REST** pour l'export/import/mise à jour de modèles (`POST /api/templates`, `GET /api/templates/{code}`, `PUT /api/templates/{code}`, `GET /api/templates/{code}/version`)

### Ashfall Connect

- **Liaison de compte** : depuis « Réglages → Ashfall Connect », le plugin demande au relais un code à 8 caractères que l'utilisateur colle sur [Ashfall Connect](https://connect.ashfall-codex.dev/link). Le compte est ancré sur le jeton d'autorisation local — le relais n'en connaît que le hash SHA-256 et émet en échange un identifiant public opaque (`ME-XXXXXXXX`)
- **Coffre synchronisé** : fiches de stats et modèles d'événement sont poussés vers le relais à chaque sauvegarde, et récupérés au démarrage puis toutes les 5 minutes
- **Édition depuis le web** : les mêmes fiches et modèles sont modifiables depuis la section MasterEvent d'Ashfall Connect ; les changements redescendent en jeu à la synchronisation suivante
- **Suppressions propagées** dans les deux sens via des marqueurs de suppression conservés 30 jours
- **Interrupteur global** : la synchronisation se coupe à tout moment sans perte de données de part et d'autre
- **Garde-fou** : tout contenu ressemblant à un secret (hash SHA-256, jeton, mot de passe) bloque l'envoi
- API REST du coffre : `POST /api/account/register`, `GET /api/cloud/documents`, `PUT|DELETE /api/cloud/documents/{kind}/{name}`, `POST /api/connect/generate-link-code`, `GET /api/connect/link-status/{code}`, `GET /api/connect/my-status`

### Météo et heure éorzéenne

- **Contrôle de la météo** : changement du temps affiché en jeu (dégagé, pluie, orage, brouillard…)
- **Contrôle de l'heure** : gel de l'heure éorzéenne à une valeur choisie (0h-23h)
- **Entièrement autonome** : aucune dépendance externe (ni Weatherman, ni Brio). Hook direct sur les fonctions du jeu + patch mémoire sur le rendu
- **Avertissement intégré** en début d'onglet : la coexistence avec d'autres plugins qui modifient la météo (Weatherman, Brio en mode GPose, etc.) peut entrer en conflit
- **Listes de météo par zone** chargées depuis les données Lumina du jeu
- **Synchronisation** : météo et heure diffusées à tous les joueurs connectés via le relay
- **Réinitialisation** : retour instantané à la météo naturelle et à l'heure normale du jeu (force le recalcul via `WeatherManager.GetCurrentWeather()`)

### Presets

- Sauvegarde de l'état complet des marqueurs en preset nommé
- Chargement et suppression de presets
- Stockage local en JSON

### Localisation

- **Français** (langue par défaut)
- **English**
- Changement de langue à chaud depuis les paramètres

### Conformité RGPD

- **Consentement intégré** dans l'assistant de configuration au premier lancement
- Consentement versionné (v3) et révocable depuis les réglages
- Données de session supprimées à la déconnexion ; seuls les modèles partagés en permanence sont conservés sur le serveur
- Journalisation anonymisée (hash SHA-256 uniquement, rotation quotidienne avec rétention 7 jours)
- Information complète sur les droits (accès, effacement, opposition)
- Données transmises : nom de personnage, identifiant de groupe, données des marqueurs (PV, PE, attitude, bouclier), fiches de personnage, jets de dés, modèles, paramètres de météo, identifiant anonymisé, jeton d'autorisation du MJ (envoyé en clair sur TLS, jamais stocké en clair côté serveur — seul son hash SHA-256 l'est)

## Architecture

Le projet est composé de deux parties :

| Composant | Technologie | Description |
|---|---|---|
| `MasterEvent/` | C# / .NET 10 / Dalamud API 15 | Plugin FFXIV (tourne dans le jeu) |
| `MasterEventRelay/` | Rust / Axum / SQLite | Serveur relais de synchronisation |

### Plugin (C#)

- **Point d'entrée** : `Plugin.cs` — enregistre la commande `/masterevent` (+ alias), les hooks UI et le tick framework
- **Rôles** : Chef de groupe = MJ, autres = Joueurs. Mode solo = MJ local
- **Communication** : Messages JSON via WebSocket, thread-safe avec `ConcurrentQueue`
- **UI** : ImGui avec thème rouge/sombre, fenêtres MJ, Joueur et Notes séparées, assistant de configuration dédié, overlay d'annonce, overlay de tour et bandeau tactique. Les overlays ne sont peints qu'une fois un personnage en jeu
- **PNJ** (`Services/Npc/`) : `NpcManager` (cycle de vie, 8 instances maximum), `NpcInstance` (écriture directe des structures du jeu pour l'apparence, l'emote et la position), `NpcSpawnGuard` (contextes interdits), `NpcSyncCoordinator` (émission et réception des répliques), `NpcPresetStore` (presets locaux)
- **Combat** : `TacticalOverlay` (bandeau et barres de vie flottantes), `TacticalCameraService` (hook sur la rotation automatique de caméra), `MovementTracker` (quota en yalms et tracé au sol), `CombatNamePlateService`, `PlayDeadService`
- **Modèles** : `EventTemplate` (définition d'événement, avec versioning et statut abonnement), `PlayerSheet` (fiche personnage, synchronisée avec son modèle parent), `StatDefinition` / `StatValue`, `CounterDefinition` / `CustomCounter`, `TurnState` / `TurnEntry` / `TurnGroup`, `SharedTemplate`, `NpcAppearance` / `NpcSyncData` / `NpcPreset`, `NotesDocument`
- **Persistance** : Config Dalamud (jeton d'autorisation du MJ, paramètres généraux), presets/modèles/fiches/partages en JSON local via un helper unifié `JsonFileStore`

### Serveur relais (Rust)

- **Axum** + **Tokio** pour les WebSocket et HTTP asynchrones
- **SQLite** (rusqlite) pour le stockage persistant des modèles, avec migration idempotente au démarrage
- Salles par `partyId` ou par code de lobby, expiration après inactivité configurable
- **Couche lobby (protocole 2)** : file d'admission par salle (32 demandes maximum), rattachement des sous-groupes par roster (64 hashes maximum), index `partyId` vers lobby pour la redirection automatique, messages `lobbyPending` / `admit` / `deny` / `lobbyMoved` / `rosterUpdate`
- **Plancher de version** : `MIN_VERSION` refuse les clients trop anciens, une valeur vide laissant tout passer
- Cache d'état pour récupération de session, jamais persisté sur disque
- **Stockage de modèles** avec codes courts, versioning, statut permanent et hash SHA-256 du créateur
- **Comptes et coffre cloud** (`me_account`, `me_document`) : identifiant public opaque par installation, documents versionnés avec marqueurs de suppression, façade `/api/connect/*` pour Ashfall Connect protégée par secret partagé en comparaison à durée constante
- Nettoyage automatique des rooms (5 min) et modèles expirés (1h)
- **Rate limiting** : 30 messages/s par client connecté, 10 nouvelles connexions/min par IP, 5 créations de salle/h par IP
- Endpoint `/health` (statut + nombre de sessions actives) et `/metrics` (format Prometheus : sessions, clients, uptime, templates, compteurs de messages et d'erreurs)
- Rotation quotidienne des logs avec rétention 7 jours
- **Shutdown gracieux** sur SIGINT / SIGTERM : notification des sessions WS puis drain court avant extinction
- TLS via reverse proxy (Caddy)

## Build

### Plugin
```bash
dotnet build MasterEvent/MasterEvent.csproj
```
Nécessite .NET 10.x SDK et Dalamud API 15 (via XIV on Mac ou équivalent).

### Serveur relais
```bash
cd MasterEventRelay
cargo build --release
./target/release/master-event-relay
```
Copier `.env.example` en `.env` pour la configuration (`PORT`, `HOST`, `ROOM_EXPIRY_MS`, `TEMPLATE_EXPIRY_MS`, `LOG_LEVEL`, `DATABASE_PATH`, `MIN_VERSION`, `MAX_ROOMS`, `ALLOWED_ORIGINS`).

## Commandes

| Commande | Description |
|---|---|
| `/masterevent` | Ouvre la fenêtre principale (MJ ou joueur selon le rôle) |
| `/masterevent joueur` | Ouvre/ferme la vue joueur |
| `/masterevent overlay` | Active/désactive le bandeau tactique |
| `/masterevent camera` | Active/désactive la caméra tactique |
| `/masterevent config` | Ouvre les paramètres |
| `/masterevent help` | Affiche l'aide |
| `/mevent` | Alias court de `/masterevent` (accepte les mêmes sous-commandes) |

### Commandes debug (mode debug activé)

| Commande                 | Description         |
|--------------------------|---------------------|
| `/masterevent connect    | /mevent connect`    | Connexion manuelle au relais |
| `/masterevent disconnect | /mevent disconnect` | Déconnexion du relais |
| `/masterevent mj         | /mevent mj`         | Basculer en vue MJ    |

## Licence

Voir le fichier [LICENSE](LICENSE).

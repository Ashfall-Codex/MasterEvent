require("dotenv").config();
const http = require("http");
const { WebSocketServer } = require("ws");
const winston = require("winston");

const PORT = parseInt(process.env.PORT || "8765", 10);
const HOST = process.env.HOST || "0.0.0.0";
const ROOM_EXPIRY_MS = parseInt(process.env.ROOM_EXPIRY_MS || "3600000", 10);
const TEMPLATE_EXPIRY_MS = parseInt(process.env.TEMPLATE_EXPIRY_MS || String(7 * 24 * 3600000), 10); // 7 jours
const LOG_LEVEL = process.env.LOG_LEVEL || "info";

const logger = winston.createLogger({
  level: LOG_LEVEL,
  format: winston.format.combine(
    winston.format.timestamp({ format: "YYYY-MM-DD HH:mm:ss" }),
    winston.format.printf(({ timestamp, level, message }) => `${timestamp} [${level.toUpperCase()}] ${message}`)
  ),
  transports: [
    new winston.transports.Console(),
    new winston.transports.File({ filename: "relay.log", maxsize: 5242880, maxFiles: 3 }),
  ],
});

// room = { clients: Map<ws, {playerHash, playerName, isLeader, version}>, lastActivity: Date, cachedState: object|null }
const rooms = new Map();

// Stockage des modèles partagés par code court
// templateStore = Map<code, { data: object, createdAt: number }>
const templateStore = new Map();
const TEMPLATE_CODE_LENGTH = 6;
const TEMPLATE_MAX_SIZE = 64 * 1024; // 64 Ko max
const TEMPLATE_MAX_COUNT = 10000; // Nombre max de modèles stockés en mémoire
const WS_MAX_PAYLOAD = 256 * 1024; // 256 Ko max par message WS
const WS_RATE_LIMIT = 30; // Messages max par seconde par client
const WS_RATE_WINDOW_MS = 1000;

function generateTemplateCode() {
  const chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // Pas de 0/O/1/I pour éviter la confusion
  let code;
  do {
    code = "";
    for (let i = 0; i < TEMPLATE_CODE_LENGTH; i++) {
      code += chars[Math.floor(Math.random() * chars.length)];
    }
  } while (templateStore.has(code));
  return code;
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    let body = "";
    let size = 0;
    req.on("data", (chunk) => {
      size += chunk.length;
      if (size > TEMPLATE_MAX_SIZE) {
        reject(new Error("Body too large"));
        req.destroy();
        return;
      }
      body += chunk;
    });
    req.on("end", () => resolve(body));
    req.on("error", reject);
  });
}

// HTTP server
const server = http.createServer(async (req, res) => {
  // Health check
  if (req.method === "GET" && req.url === "/health") {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ status: "ok" }));
    return;
  }

  // Publier un modèle → retourne un code court
  if (req.method === "POST" && req.url === "/api/templates") {
    try {
      const body = await readBody(req);
      const template = JSON.parse(body);
      if (!template.Name) {
        res.writeHead(400, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ error: "Missing template Name" }));
        return;
      }
      if (templateStore.size >= TEMPLATE_MAX_COUNT) {
        res.writeHead(503, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ error: "Template store full" }));
        return;
      }
      const permanent = !!template.permanent;
      delete template.permanent; // Ne pas stocker le flag dans les données
      const code = generateTemplateCode();
      templateStore.set(code, { data: template, createdAt: Date.now(), permanent });
      logger.info(`Template stored: ${code} (${template.Name}, permanent: ${permanent})`);
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ code }));
    } catch (err) {
      res.writeHead(400, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ error: err.message }));
    }
    return;
  }

  // Récupérer un modèle par code
  const templateMatch = req.method === "GET" && req.url?.match(/^\/api\/templates\/([A-Z0-9]+)$/);
  if (templateMatch) {
    const code = templateMatch[1];
    const entry = templateStore.get(code);
    if (!entry) {
      res.writeHead(404, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ error: "Template not found" }));
      return;
    }
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify(entry.data));
    return;
  }

  res.writeHead(404);
  res.end();
});

const wss = new WebSocketServer({ server, maxPayload: WS_MAX_PAYLOAD });

server.listen(PORT, HOST, () => {
  logger.info(`MasterEvent Relay listening on ${HOST}:${PORT}`);
});

wss.on("connection", (ws) => {
  let clientRoom = null;
  let clientInfo = null;
  let messageCount = 0;
  let rateLimitWindowStart = Date.now();

  ws.on("message", (raw) => {
    // Rate limiting par client
    const now = Date.now();
    if (now - rateLimitWindowStart > WS_RATE_WINDOW_MS) {
      messageCount = 0;
      rateLimitWindowStart = now;
    }
    messageCount++;
    if (messageCount > WS_RATE_LIMIT) {
      logger.warn(`Rate limit exceeded for ${clientInfo?.playerHash || "unknown"}`);
      return;
    }

    let msg;
    try {
      msg = JSON.parse(raw);
    } catch {
      return;
    }
    if (!msg || typeof msg.type !== "string") return;

    switch (msg.type) {
      case "join":
        handleJoin(ws, msg);
        break;
      case "leave":
        handleLeave(ws, true);
        break;
      case "update":
      case "clear":
      case "playerUpdate":
      case "templateShare":
      case "turnUpdate":
      case "turnClear":
        // Leader ou joueur promu par le leader
        if (clientInfo && (clientInfo.isLeader || clientInfo.isPromoted)) {
          relayToRoom(ws, msg);
        }
        break;
      case "requestUpdate":
      case "roll":
      case "statRoll":
      case "playerStatUpdate":
        relayToRoom(ws, msg);
        break;
      case "promote":
        if (clientInfo && clientInfo.isLeader) {
          handlePromote(ws, msg);
        }
        break;
      default:
        break;
    }
  });

  ws.on("close", () => {
    handleLeave(ws, false);
  });

  ws.on("error", (err) => {
    logger.error(`WebSocket error: ${err.message}`);
    handleLeave(ws, false);
  });

  function handleJoin(ws, msg) {
    const { partyId, playerName, playerHash, isLeader, version } = msg;
    if (!partyId || !playerName) return;
    if (typeof partyId !== "string" || typeof playerName !== "string") return;
    if (partyId.length > 64 || playerName.length > 128) return;

    const roomKey = String(partyId);
    const hash = (typeof playerHash === "string" ? playerHash : "anon").slice(0, 32);

    // Leave previous room if any
    handleLeave(ws, true);

    // Create or get room
    if (!rooms.has(roomKey)) {
      rooms.set(roomKey, { clients: new Map(), lastActivity: Date.now(), cachedState: null });
    }
    const room = rooms.get(roomKey);

    // Validation du leadership : seul le premier leader d'une room est accepté, ou si aucun leader n'est actuellement connecté
    const existingLeader = [...room.clients.values()].find(c => c.isLeader);
    const grantLeader = !!isLeader && !existingLeader;

    clientInfo = { playerName, playerHash: hash, isLeader: grantLeader, isPromoted: false, version: version || "0" };
    clientRoom = roomKey;
    room.clients.set(ws, clientInfo);
    room.lastActivity = Date.now();

    // Version check: warn if joining player has a different version than existing members
    const clientVersion = version || "0";
    for (const [existingClient, existingInfo] of room.clients) {
      if (existingClient !== ws && existingInfo.version !== clientVersion) {
        ws.send(
          JSON.stringify({
            type: "versionMismatch",
            playerName: existingInfo.playerName,
            version: existingInfo.version,
          })
        );
        logger.warn(`Version mismatch: ${hash} (v${clientVersion}) vs ${existingInfo.playerHash} (v${existingInfo.version}) in room ${roomKey}`);
        break;
      }
    }

    // Notify the room that a new player joined (playerName is relayed to clients, never logged)
    for (const [client, info] of room.clients) {
      if (client !== ws && client.readyState === 1) {
        client.send(
          JSON.stringify({
            type: "playerJoined",
            playerName,
            playerHash: hash,
            playerCount: room.clients.size,
          })
        );
      }
    }

    // Send cached state if available
    if (room.cachedState) {
      if (grantLeader) {
        // Leader reconnecting: send as cachedState so they can restore
        ws.send(JSON.stringify({ ...room.cachedState, type: "cachedState" }));
      } else {
        // Player joining without a leader present: send cached state as update
        const hasLeader = [...room.clients.values()].some(c => c !== clientInfo && c.isLeader);
        if (!hasLeader) {
          ws.send(JSON.stringify({ ...room.cachedState, type: "update" }));
        }
      }
    }

    // Send confirmation to the joiner
    ws.send(
      JSON.stringify({
        type: "joinConfirm",
        roomKey,
        playerCount: room.clients.size,
        isLeader: clientInfo.isLeader,
      })
    );

    // Log with hash only — never log playerName
    logger.info(`${hash} joined room ${roomKey} (${room.clients.size} members, leader: ${grantLeader}, v${clientVersion})`);
  }

  function handleLeave(ws, voluntary) {
    if (!clientRoom) return;
    const room = rooms.get(clientRoom);
    if (!room) {
      clientRoom = null;
      clientInfo = null;
      return;
    }

    const info = room.clients.get(ws);
    room.clients.delete(ws);

    // Notify remaining clients (playerName relayed to clients, never logged)
    for (const [client] of room.clients) {
      if (client.readyState === 1) {
        client.send(
          JSON.stringify({
            type: "playerLeft",
            playerName: info?.playerName || "?",
            playerHash: info?.playerHash || "?",
            playerCount: room.clients.size,
          })
        );
      }
    }

    // Log with hash only
    logger.info(`${info?.playerHash || "?"} left room ${clientRoom} (${room.clients.size} remaining, voluntary: ${!!voluntary})`);

    // Clean up empty rooms
    if (room.clients.size === 0) {
      if (!voluntary && room.cachedState) {
        // Crash/disconnect: keep room alive for GM recovery, periodic cleanup will handle expiry
        logger.info(`Room ${clientRoom} empty but keeping cached state for crash recovery`);
      } else {
        rooms.delete(clientRoom);
        logger.info(`Room ${clientRoom} deleted (empty)`);
      }
    }

    clientRoom = null;
    clientInfo = null;
  }

  function handlePromote(ws, msg) {
    if (!clientRoom) return;
    const room = rooms.get(clientRoom);
    if (!room) return;

    // Mettre à jour le statut isPromoted côté serveur pour le joueur ciblé
    const targetHash = typeof msg.targetHash === "string" ? msg.targetHash.slice(0, 32) : null;
    if (targetHash) {
      for (const [, info] of room.clients) {
        if (info.playerHash === targetHash) {
          info.isPromoted = !!msg.canEdit;
          logger.info(`Player ${targetHash} ${info.isPromoted ? "promoted" : "demoted"} in room ${clientRoom}`);
          break;
        }
      }
    }

    // Relayer le message aux autres clients
    relayToRoom(ws, msg);
  }

  function relayToRoom(ws, msg) {
    if (!clientRoom) return;
    const room = rooms.get(clientRoom);
    if (!room) return;

    room.lastActivity = Date.now();

    // Cache update messages for crash recovery; clear on clear
    if (msg.type === "update") room.cachedState = msg;
    if (msg.type === "clear") room.cachedState = null;

    const payload = JSON.stringify(msg);

    for (const [client] of room.clients) {
      if (client !== ws && client.readyState === 1) {
        client.send(payload);
      }
    }
  }
});

// Periodic room cleanup
setInterval(() => {
  const now = Date.now();
  for (const [key, room] of rooms) {
    if (now - room.lastActivity > ROOM_EXPIRY_MS) {
      for (const [client] of room.clients) {
        client.close(1000, "Room expired");
      }
      rooms.delete(key);
      logger.info(`Room ${key} expired and cleaned up`);
    }
  }
}, 5 * 60 * 1000);

// Nettoyage périodique des modèles expirés
setInterval(() => {
  const now = Date.now();
  for (const [code, entry] of templateStore) {
    if (!entry.permanent && now - entry.createdAt > TEMPLATE_EXPIRY_MS) {
      templateStore.delete(code);
      logger.info(`Template ${code} expired and cleaned up`);
    }
  }
}, 60 * 60 * 1000); // Vérification toutes les heures

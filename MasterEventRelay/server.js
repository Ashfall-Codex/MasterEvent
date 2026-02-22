require("dotenv").config();
const http = require("http");
const { WebSocketServer } = require("ws");
const winston = require("winston");

const PORT = parseInt(process.env.PORT || "8765", 10);
const HOST = process.env.HOST || "0.0.0.0";
const ROOM_EXPIRY_MS = parseInt(process.env.ROOM_EXPIRY_MS || "3600000", 10);
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

// HTTP server with /health endpoint
const server = http.createServer((req, res) => {
  if (req.method === "GET" && req.url === "/health") {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ status: "ok" }));
    return;
  }
  res.writeHead(404);
  res.end();
});

const wss = new WebSocketServer({ server });

server.listen(PORT, HOST, () => {
  logger.info(`MasterEvent Relay listening on ${HOST}:${PORT}`);
});

wss.on("connection", (ws) => {
  let clientRoom = null;
  let clientInfo = null;

  ws.on("message", (raw) => {
    let msg;
    try {
      msg = JSON.parse(raw);
    } catch {
      return;
    }

    switch (msg.type) {
      case "join":
        handleJoin(ws, msg);
        break;
      case "leave":
        handleLeave(ws, true);
        break;
      case "update":
      case "clear":
      case "requestUpdate":
      case "roll":
      case "playerUpdate":
      case "templateShare":
        relayToRoom(ws, msg);
        break;
      case "promote":
        if (clientInfo && clientInfo.isLeader) {
          relayToRoom(ws, msg);
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

    const roomKey = String(partyId);
    const hash = playerHash || "anon";

    // Leave previous room if any
    handleLeave(ws, true);

    // Create or get room
    if (!rooms.has(roomKey)) {
      rooms.set(roomKey, { clients: new Map(), lastActivity: Date.now(), cachedState: null });
    }
    const room = rooms.get(roomKey);

    clientInfo = { playerName, playerHash: hash, isLeader: !!isLeader, version: version || "0" };
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
      if (isLeader) {
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
    logger.info(`${hash} joined room ${roomKey} (${room.clients.size} members, leader: ${isLeader}, v${clientVersion})`);
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

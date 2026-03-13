import { Buffer } from "node:buffer";

let cachedWebSocketConstructorPromise = null;

export async function resolveWebSocketConstructor() {
  if (typeof globalThis.WebSocket === "function") {
    return globalThis.WebSocket;
  }

  cachedWebSocketConstructorPromise ??= import("../playwright/node_modules/playwright-core/lib/utilsBundle.js")
    .then((module) => module?.ws ?? module?.default?.ws ?? null)
    .catch(() => null);

  const WebSocketConstructor = await cachedWebSocketConstructorPromise;
  if (typeof WebSocketConstructor === "function") {
    return WebSocketConstructor;
  }

  throw new Error(
    "No WebSocket implementation is available. Use Node with global WebSocket support or install Simulators/playwright dependencies first.",
  );
}

export async function connectWebSocket(url, protocols, { timeoutMs = 10000, label = "ws" } = {}) {
  const WebSocketConstructor = await resolveWebSocketConstructor();
  const ws = new WebSocketConstructor(url, protocols);

  await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      cleanup();
      reject(new Error(`[${label}] connect timeout`));
    }, timeoutMs);

    const cleanup = () => {
      clearTimeout(timeout);
      removeOpenListener?.();
      removeErrorListener?.();
    };

    const removeOpenListener = addWebSocketListener(ws, "open", () => {
      cleanup();
      resolve();
    }, { once: true });

    const removeErrorListener = addWebSocketListener(ws, "error", (error) => {
      cleanup();
      reject(new Error(`[${label}] websocket error${formatWebSocketError(error)}`));
    }, { once: true });
  });

  return ws;
}

export function attachWebSocketMessageHandler(ws, handler) {
  return addWebSocketListener(ws, "message", (...args) => {
    handler(normalizeWebSocketMessage(...args));
  });
}

export function isWebSocketOpen(ws) {
  return !!ws && ws.readyState === getWebSocketOpenState(ws);
}

function getWebSocketOpenState(ws) {
  return ws?.constructor?.OPEN ?? globalThis.WebSocket?.OPEN ?? 1;
}

function normalizeWebSocketMessage(...args) {
  let payload = args[0];

  if (payload && typeof payload === "object" && "data" in payload) {
    payload = payload.data;
  }

  if (Buffer.isBuffer(payload)) {
    return payload.toString();
  }

  if (payload instanceof ArrayBuffer) {
    return Buffer.from(payload).toString();
  }

  if (ArrayBuffer.isView(payload)) {
    return Buffer.from(payload.buffer, payload.byteOffset, payload.byteLength).toString();
  }

  return String(payload ?? "");
}

function addWebSocketListener(ws, eventName, handler, { once = false } = {}) {
  if (typeof ws.addEventListener === "function") {
    const wrapped = (...args) => {
      if (once) {
        ws.removeEventListener(eventName, wrapped);
      }
      handler(...args);
    };
    ws.addEventListener(eventName, wrapped);
    return () => ws.removeEventListener?.(eventName, wrapped);
  }

  if (once && typeof ws.once === "function") {
    ws.once(eventName, handler);
    return () => removeNodeListener(ws, eventName, handler);
  }

  if (typeof ws.on === "function") {
    ws.on(eventName, handler);
    return () => removeNodeListener(ws, eventName, handler);
  }

  const propertyName = `on${eventName}`;
  const previousHandler = ws[propertyName];
  const wrapped = (...args) => {
    if (once && ws[propertyName] === wrapped) {
      ws[propertyName] = previousHandler;
    }

    if (typeof previousHandler === "function") {
      previousHandler(...args);
    }

    handler(...args);
  };

  ws[propertyName] = wrapped;
  return () => {
    if (ws[propertyName] === wrapped) {
      ws[propertyName] = previousHandler;
    }
  };
}

function removeNodeListener(ws, eventName, handler) {
  if (typeof ws.off === "function") {
    ws.off(eventName, handler);
  } else if (typeof ws.removeListener === "function") {
    ws.removeListener(eventName, handler);
  }
}

function formatWebSocketError(error) {
  if (!error) {
    return "";
  }

  if (typeof error === "string") {
    return `: ${error}`;
  }

  if (typeof error.message === "string" && error.message.trim().length > 0) {
    return `: ${error.message}`;
  }

  if (typeof error.type === "string" && error.type.trim().length > 0) {
    return `: ${error.type}`;
  }

  return "";
}

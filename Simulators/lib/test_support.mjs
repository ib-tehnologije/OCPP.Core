import { attachWebSocketMessageHandler, connectWebSocket, isWebSocketOpen } from "./websocket_support.mjs";

export function nowIsoUtc() {
  return new Date().toISOString().replace(/\.\d{3}Z$/, "Z");
}

export function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

export function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

export async function waitFor(predicate, { timeoutMs = 30000, intervalMs = 250, errorMessage = "Timed out" } = {}) {
  const startedAt = Date.now();
  while (Date.now() - startedAt < timeoutMs) {
    const result = await predicate();
    if (result) {
      return result;
    }

    await sleep(intervalMs);
  }

  throw new Error(errorMessage);
}

export async function httpJson(url, { method = "GET", headers = {}, body } = {}) {
  const response = await fetch(url, {
    method,
    headers,
    body,
    redirect: "follow",
  });
  const text = await response.text();
  let json = null;
  try {
    json = text ? JSON.parse(text) : null;
  } catch {
    json = null;
  }

  return {
    ok: response.ok,
    status: response.status,
    text,
    json,
    headers: response.headers,
  };
}

export async function httpText(url, { method = "GET", headers = {}, body } = {}) {
  const response = await fetch(url, {
    method,
    headers,
    body,
    redirect: "follow",
  });

  return {
    ok: response.ok,
    status: response.status,
    text: await response.text(),
    headers: response.headers,
  };
}

export function guidLike() {
  const s4 = () => Math.floor((1 + Math.random()) * 0x10000).toString(16).slice(1);
  return `${s4()}${s4()}-${s4()}-${s4()}-${s4()}-${s4()}${s4()}${s4()}`;
}

function newId(prefix = "") {
  return `${prefix}${Math.random().toString(16).slice(2)}${Math.random().toString(16).slice(2)}`.slice(0, 32);
}

export class OcppClient {
  constructor({ url, protocols, label }) {
    this.url = url;
    this.protocols = protocols;
    this.label = label;
    this.ws = null;
    this.pending = new Map();
    this.callHandlers = new Map();
  }

  onCall(action, handler) {
    this.callHandlers.set(action, handler);
  }

  async connect({ timeoutMs = 10000 } = {}) {
    this.ws = await connectWebSocket(this.url, this.protocols, {
      timeoutMs,
      label: this.label,
    });
    const ws = this.ws;
    attachWebSocketMessageHandler(ws, (data) => this.#handleMessage(data));
  }

  close(code = 3001, reason = "") {
    try {
      this.ws?.close(code, reason);
    } catch {
      // best effort close
    }
  }

  async call(action, payload, { timeoutMs = 15000 } = {}) {
    const id = newId("c");
    const ws = this.ws;
    assert(isWebSocketOpen(ws), `[${this.label}] websocket not open`);

    const pending = new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`[${this.label}] ${action} timed out`));
      }, timeoutMs);

      this.pending.set(id, { resolve, reject, timeout, action });
    });

    ws.send(JSON.stringify([2, id, action, payload ?? {}]));
    return pending;
  }

  sendRaw(payload) {
    const ws = this.ws;
    if (!isWebSocketOpen(ws)) {
      return false;
    }
    ws.send(JSON.stringify(payload));
    return true;
  }

  async #handleMessage(raw) {
    const message = JSON.parse(raw);
    const type = message?.[0];
    const uniqueId = message?.[1];

    if (type === 3) {
      const pending = this.pending.get(uniqueId);
      if (pending) {
        clearTimeout(pending.timeout);
        this.pending.delete(uniqueId);
        pending.resolve(message[2]);
      }
      return;
    }

    if (type === 4) {
      const pending = this.pending.get(uniqueId);
      if (pending) {
        clearTimeout(pending.timeout);
        this.pending.delete(uniqueId);
        pending.reject(new Error(`[${this.label}] CALLERROR for ${pending.action}: ${message[2]} ${message[3]}`));
      }
      return;
    }

    if (type === 2) {
      const action = message?.[2];
      const payload = message?.[3] ?? {};
      const handler = this.callHandlers.get(action);
      if (!handler) {
        this.sendRaw([4, uniqueId, "NotImplemented", `No handler for ${action}`, {}]);
        return;
      }

      try {
        const responsePayload = await handler(uniqueId, payload);
        this.sendRaw([3, uniqueId, responsePayload ?? {}]);
      } catch (error) {
        this.sendRaw([4, uniqueId, "InternalError", String(error?.message ?? error), {}]);
      }
    }
  }
}

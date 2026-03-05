const SERVER_WS_BASE = process.env.SERVER_WS_BASE ?? 'ws://localhost:8081/OCPP';
const CP_ID = process.env.CP_ID ?? 'Test1234';
const CONNECTOR_ID = Number(process.env.CONNECTOR_ID ?? '1');
const TAG = process.env.TAG ?? 'PAYFLOW';

function nowIsoUtc() {
  return new Date().toISOString().replace(/\.\d{3}Z$/, 'Z');
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

function newId(prefix = '') {
  return `${prefix}${Math.random().toString(16).slice(2)}${Math.random().toString(16).slice(2)}`.slice(0, 32);
}

class OcppClient {
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

  async connect({ timeoutMs = 8000 } = {}) {
    assert(typeof WebSocket === 'function', 'Global WebSocket is not available in this Node runtime.');
    this.ws = new WebSocket(this.url, this.protocols);
    const ws = this.ws;
    await new Promise((resolve, reject) => {
      const timeout = setTimeout(() => reject(new Error(`[${this.label}] connect timeout`)), timeoutMs);
      ws.onopen = () => {
        clearTimeout(timeout);
        resolve();
      };
      ws.onerror = () => {
        clearTimeout(timeout);
        reject(new Error(`[${this.label}] websocket error`));
      };
    });
    ws.onmessage = (event) => this.#handleMessage(event.data);
  }

  close(code = 3001, reason = '') {
    try {
      this.ws?.close(code, reason);
    } catch {}
  }

  sendRaw(obj) {
    assert(this.ws && this.ws.readyState === 1, `[${this.label}] websocket not open`);
    this.ws.send(JSON.stringify(obj));
  }

  async call(action, payload, { timeoutMs = 15000 } = {}) {
    const id = newId('c');
    const ws = this.ws;
    assert(ws && ws.readyState === 1, `[${this.label}] websocket not open`);

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

  async #handleMessage(raw) {
    const msg = JSON.parse(raw);
    const type = msg?.[0];
    const uniqueId = msg?.[1];

    if (type === 3) {
      const pending = this.pending.get(uniqueId);
      if (pending) {
        clearTimeout(pending.timeout);
        this.pending.delete(uniqueId);
        pending.resolve(msg[2]);
      }
      return;
    }

    if (type === 4) {
      const pending = this.pending.get(uniqueId);
      if (pending) {
        clearTimeout(pending.timeout);
        this.pending.delete(uniqueId);
        pending.reject(new Error(`[${this.label}] CALLERROR for ${pending.action}: ${msg[2]} ${msg[3]}`));
      }
      return;
    }

    if (type === 2) {
      const action = msg?.[2];
      const payload = msg?.[3] ?? {};
      const handler = this.callHandlers.get(action);
      if (!handler) {
        this.sendRaw([4, uniqueId, 'NotImplemented', `No handler for ${action}`, {}]);
        return;
      }

      try {
        const responsePayload = await handler(uniqueId, payload);
        this.sendRaw([3, uniqueId, responsePayload ?? {}]);
      } catch (error) {
        this.sendRaw([4, uniqueId, 'InternalError', String(error?.message ?? error), {}]);
      }
    }
  }
}

async function main() {
  const client = new OcppClient({
    url: `${SERVER_WS_BASE}/${encodeURIComponent(CP_ID)}`,
    protocols: ['ocpp1.6', 'ocpp1.5'],
    label: `PAYFLOW:${CP_ID}`,
  });

  let transactionId = null;
  let remoteStarted = false;
  let finished = false;

  client.onCall('RemoteStartTransaction', async (_uniqueId, payload) => {
    const connectorId = payload?.connectorId ?? CONNECTOR_ID;
    const idTag = payload?.idTag ?? TAG;
    remoteStarted = true;

    const startResponse = await client.call('StartTransaction', {
      connectorId,
      idTag,
      meterStart: 0,
      timestamp: nowIsoUtc(),
    });

    transactionId = startResponse?.transactionId ?? startResponse?.TransactionId ?? null;

    await client.call('StatusNotification', {
      connectorId,
      errorCode: 'NoError',
      status: 'Charging',
      timestamp: nowIsoUtc(),
    });

    await client.call('MeterValues', {
      connectorId,
      transactionId,
      meterValue: [
        {
          timestamp: nowIsoUtc(),
          sampledValue: [{ value: '5', measurand: 'Energy.Active.Import.Register', unit: 'kWh' }],
        },
      ],
    });

    await sleep(1500);

    await client.call('StopTransaction', {
      transactionId,
      meterStop: 5,
      timestamp: nowIsoUtc(),
      reason: 'Local',
    });

    await client.call('StatusNotification', {
      connectorId,
      errorCode: 'NoError',
      status: 'Available',
      timestamp: nowIsoUtc(),
    });

    finished = true;
    return { status: 'Accepted' };
  });

  await client.connect();

  const bootResponse = await client.call('BootNotification', {
    chargePointVendor: 'TestVendor',
    chargePointModel: 'PaymentFlowModel',
  });
  assert(bootResponse?.status === 'Accepted', `BootNotification failed: ${JSON.stringify(bootResponse)}`);

  await client.call('StatusNotification', {
    connectorId: CONNECTOR_ID,
    errorCode: 'NoError',
    status: 'Available',
    timestamp: nowIsoUtc(),
  });

  const startedAt = Date.now();
  while (!finished && Date.now() - startedAt < 120000) {
    await client.call('StatusNotification', {
      connectorId: CONNECTOR_ID,
      errorCode: 'NoError',
      status: 'Preparing',
      timestamp: nowIsoUtc(),
    }).catch(() => {});

    if (remoteStarted && transactionId) {
      break;
    }

    await sleep(1500);
  }

  const pollStartedAt = Date.now();
  while (!finished && Date.now() - pollStartedAt < 30000) {
    await sleep(1000);
  }

  console.log(JSON.stringify({ cpId: CP_ID, connectorId: CONNECTOR_ID, remoteStarted, transactionId, finished }, null, 2));
  client.close();
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});

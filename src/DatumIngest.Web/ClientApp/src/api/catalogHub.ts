import * as signalR from '@microsoft/signalr';
import {
  getHubProxyFactory,
  getReceiverRegister,
} from './generated/hubs/TypedSignalR.Client';
import type {
  ICatalogHub,
  ICatalogHubClient,
} from './generated/hubs/TypedSignalR.Client/DatumIngest.Web.Hubs';
import type { CatalogChangedEvent } from './generated/hubs/DatumIngest.Web.Hubs';

// Singleton HubConnection + proxy + fan-out dispatcher for the catalog-
// change push channel. Mirrors the shape of `hub.ts` but on a separate
// connection (`/hubs/catalog`) because the chat hub and the catalog hub
// have unrelated lifecycles and receiver surfaces.
//
// State modules subscribe via the helpers below; views never touch the
// hub directly. Connection is built lazily on first call so the SPA's
// first paint isn't gated on hub readiness.

let connection: signalR.HubConnection | null = null;
let connectPromise: Promise<void> | null = null;
let proxy: ICatalogHub | null = null;

// ───────────────────────── Subscriber registries ─────────────────────────

type Handler<T> = (event: T) => void;

const catalogChangedHandlers: Set<Handler<CatalogChangedEvent>> = new Set();

type CloseHandler = (err?: Error) => void;
const closeHandlers: Set<CloseHandler> = new Set();

function fanOut<T>(set: Set<Handler<T>>, event: T): void {
  for (const handler of set) {
    try {
      handler(event);
    } catch {
      // Handler bugs shouldn't break sibling handlers or future events.
    }
  }
}

// ───────────────────────── Public subscriber API ─────────────────────────

export function onCatalogChanged(
  handler: Handler<CatalogChangedEvent>,
): () => void {
  catalogChangedHandlers.add(handler);
  return () => catalogChangedHandlers.delete(handler);
}

export function onCatalogHubClosed(handler: CloseHandler): () => void {
  closeHandlers.add(handler);
  return () => closeHandlers.delete(handler);
}

export type { CatalogChangedEvent } from './generated/hubs/DatumIngest.Web.Hubs';
export { CatalogChangeKind } from './generated/hubs/DatumIngest.Web.Hubs';

// ───────────────────────── The dispatcher receiver ─────────────────────────

const dispatcher: ICatalogHubClient = {
  async onPong(): Promise<void> {
    // No subscribers today; left implemented for the typed-receiver contract.
  },
  async onCatalogChanged(event: CatalogChangedEvent): Promise<void> {
    fanOut(catalogChangedHandlers, event);
  },
};

// ───────────────────────── Connection lifecycle ─────────────────────────

export async function acquireCatalogHub(): Promise<ICatalogHub> {
  if (!connection) {
    connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/catalog')
      .withAutomaticReconnect()
      .build();
    getReceiverRegister('ICatalogHubClient').register(connection, dispatcher);
    connection.onclose((err) => {
      for (const handler of closeHandlers) {
        try {
          handler(err);
        } catch {
          // Same forgiveness as the dispatcher fan-out.
        }
      }
    });
  }
  if (!connectPromise) {
    connectPromise = connection.start();
  }
  await connectPromise;
  if (!proxy) {
    proxy = getHubProxyFactory('ICatalogHub').createHubProxy(connection);
  }
  return proxy;
}

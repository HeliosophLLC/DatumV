import * as signalR from '@microsoft/signalr';
import {
  getHubProxyFactory,
  getReceiverRegister,
} from './generated/hubs/TypedSignalR.Client';
import type {
  ICatalogHub,
  ICatalogHubClient,
} from './generated/hubs/TypedSignalR.Client/Heliosoph.DatumV.Web.Hubs';
import type {
  CatalogChangedEvent,
  ModelLoadedEvent,
  ModelEvictedEvent,
  ModelActiveChangedEvent,
  CalibrationRampStartedEvent,
  CalibrationRampStepEvent,
  CalibrationRampHaltedEvent,
  CalibrationRampCompletedEvent,
} from './generated/hubs/Heliosoph.DatumV.Web.Hubs';

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

// Files-changed has no payload — it's a "refetch /api/files" hint pushed
// by the server-side directory watcher when the catalog tree changes out
// of band (VS Code save, git checkout, hand-edit). State/files.ts is the
// sole consumer today.
type VoidHandler = () => void;
const filesChangedHandlers: Set<VoidHandler> = new Set();

// Residency lifecycle (IModelLifecycleObserver fan-out). State
// subscribers register from `state/residency.ts`; views never touch
// these directly.
const modelLoadedHandlers: Set<Handler<ModelLoadedEvent>> = new Set();
const modelEvictedHandlers: Set<Handler<ModelEvictedEvent>> = new Set();
const modelActiveChangedHandlers: Set<Handler<ModelActiveChangedEvent>> = new Set();

// Calibration ramp lifecycle (ICalibrationObserver fan-out).
const rampStartedHandlers: Set<Handler<CalibrationRampStartedEvent>> = new Set();
const rampStepHandlers: Set<Handler<CalibrationRampStepEvent>> = new Set();
const rampHaltedHandlers: Set<Handler<CalibrationRampHaltedEvent>> = new Set();
const rampCompletedHandlers: Set<Handler<CalibrationRampCompletedEvent>> = new Set();

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

export function onFilesChanged(handler: VoidHandler): () => void {
  filesChangedHandlers.add(handler);
  return () => filesChangedHandlers.delete(handler);
}

export function onModelLoaded(handler: Handler<ModelLoadedEvent>): () => void {
  modelLoadedHandlers.add(handler);
  return () => modelLoadedHandlers.delete(handler);
}

export function onModelEvicted(handler: Handler<ModelEvictedEvent>): () => void {
  modelEvictedHandlers.add(handler);
  return () => modelEvictedHandlers.delete(handler);
}

export function onModelActiveChanged(
  handler: Handler<ModelActiveChangedEvent>,
): () => void {
  modelActiveChangedHandlers.add(handler);
  return () => modelActiveChangedHandlers.delete(handler);
}

export function onCalibrationRampStarted(
  handler: Handler<CalibrationRampStartedEvent>,
): () => void {
  rampStartedHandlers.add(handler);
  return () => rampStartedHandlers.delete(handler);
}

export function onCalibrationRampStep(
  handler: Handler<CalibrationRampStepEvent>,
): () => void {
  rampStepHandlers.add(handler);
  return () => rampStepHandlers.delete(handler);
}

export function onCalibrationRampHalted(
  handler: Handler<CalibrationRampHaltedEvent>,
): () => void {
  rampHaltedHandlers.add(handler);
  return () => rampHaltedHandlers.delete(handler);
}

export function onCalibrationRampCompleted(
  handler: Handler<CalibrationRampCompletedEvent>,
): () => void {
  rampCompletedHandlers.add(handler);
  return () => rampCompletedHandlers.delete(handler);
}

export function onCatalogHubClosed(handler: CloseHandler): () => void {
  closeHandlers.add(handler);
  return () => closeHandlers.delete(handler);
}

export type {
  CatalogChangedEvent,
  ModelLoadedEvent,
  ModelEvictedEvent,
  ModelActiveChangedEvent,
  CalibrationRampStartedEvent,
  CalibrationRampStepEvent,
  CalibrationRampHaltedEvent,
  CalibrationRampCompletedEvent,
} from './generated/hubs/Heliosoph.DatumV.Web.Hubs';
export {
  CatalogChangeKind,
  ModelEvictionReason,
  CalibrationHaltReason,
} from './generated/hubs/Heliosoph.DatumV.Web.Hubs';

// ───────────────────────── The dispatcher receiver ─────────────────────────

const dispatcher: ICatalogHubClient = {
  async onPong(): Promise<void> {
    // No subscribers today; left implemented for the typed-receiver contract.
  },
  async onCatalogChanged(event: CatalogChangedEvent): Promise<void> {
    fanOut(catalogChangedHandlers, event);
  },
  async onFilesChanged(): Promise<void> {
    for (const handler of filesChangedHandlers) {
      try {
        handler();
      } catch {
        // Handler bugs shouldn't break sibling handlers or future events.
      }
    }
  },
  async onModelLoaded(event: ModelLoadedEvent): Promise<void> {
    fanOut(modelLoadedHandlers, event);
  },
  async onModelEvicted(event: ModelEvictedEvent): Promise<void> {
    fanOut(modelEvictedHandlers, event);
  },
  async onModelActiveChanged(event: ModelActiveChangedEvent): Promise<void> {
    fanOut(modelActiveChangedHandlers, event);
  },
  async onCalibrationRampStarted(event: CalibrationRampStartedEvent): Promise<void> {
    fanOut(rampStartedHandlers, event);
  },
  async onCalibrationRampStep(event: CalibrationRampStepEvent): Promise<void> {
    fanOut(rampStepHandlers, event);
  },
  async onCalibrationRampHalted(event: CalibrationRampHaltedEvent): Promise<void> {
    fanOut(rampHaltedHandlers, event);
  },
  async onCalibrationRampCompleted(event: CalibrationRampCompletedEvent): Promise<void> {
    fanOut(rampCompletedHandlers, event);
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

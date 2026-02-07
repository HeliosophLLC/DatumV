import * as signalR from '@microsoft/signalr';
import {
  getHubProxyFactory,
  getReceiverRegister,
} from './generated/hubs/TypedSignalR.Client';
import type {
  IStreamHub,
  IStreamHubClient,
} from './generated/hubs/TypedSignalR.Client/DatumIngest.Web.Hubs';
import type {
  ModelDownloadStarted,
  ModelDownloadProgress,
  ModelDownloadComplete,
  ModelDownloadFailed,
} from './generated/hubs/DatumIngest.Web.ModelLibrary';

// Singleton HubConnection + proxy + a fan-out dispatcher.
//
// One receiver is registered against the SignalR connection for the app's
// lifetime; that receiver's methods fan each event out to subscribers
// registered via the typed helpers below (onChatToken, onModelDownloadProgress,
// etc.). State modules subscribe by importing the helpers; views never
// touch the hub directly.
//
// The dispatcher exists because TypedSignalR's `register` takes exactly
// one receiver object, but in our app several state modules want
// different subsets of the same event stream (chat-state cares about
// onChatToken; downloads-state cares about onModelDownloadProgress; both
// share one HubConnection). Without the dispatcher, each new consumer
// would either steal events from the others or require a second
// connection. With it, adding a new consumer is `import { onX } from
// '@/api/hub'; onX(handler);` — no churn at this layer.
//
// Connection is built and started lazily on first acquireStreamHub call
// so the SPA's first paint isn't gated on hub readiness.

let connection: signalR.HubConnection | null = null;
let connectPromise: Promise<void> | null = null;
let proxy: IStreamHub | null = null;

// ───────────────────────── Subscriber registries ─────────────────────────

type Handler<T> = (event: T) => void;

const chatTokenHandlers: Set<Handler<string>> = new Set();
const chatCompleteHandlers: Set<Handler<void>> = new Set();
const chatErrorHandlers: Set<Handler<string>> = new Set();

const dlStartedHandlers: Set<Handler<ModelDownloadStarted>> = new Set();
const dlProgressHandlers: Set<Handler<ModelDownloadProgress>> = new Set();
const dlCompleteHandlers: Set<Handler<ModelDownloadComplete>> = new Set();
const dlFailedHandlers: Set<Handler<ModelDownloadFailed>> = new Set();

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

function subscribe<T>(set: Set<Handler<T>>, handler: Handler<T>): () => void {
  set.add(handler);
  return () => set.delete(handler);
}

// ───────────────────────── Public subscriber API ─────────────────────────

export const onChatToken = (handler: Handler<string>) => subscribe(chatTokenHandlers, handler);
export const onChatComplete = (handler: Handler<void>) => subscribe(chatCompleteHandlers, handler);
export const onChatError = (handler: Handler<string>) => subscribe(chatErrorHandlers, handler);

export const onModelDownloadStarted = (handler: Handler<ModelDownloadStarted>) =>
  subscribe(dlStartedHandlers, handler);
export const onModelDownloadProgress = (handler: Handler<ModelDownloadProgress>) =>
  subscribe(dlProgressHandlers, handler);
export const onModelDownloadComplete = (handler: Handler<ModelDownloadComplete>) =>
  subscribe(dlCompleteHandlers, handler);
export const onModelDownloadFailed = (handler: Handler<ModelDownloadFailed>) =>
  subscribe(dlFailedHandlers, handler);

export function onConnectionClosed(handler: CloseHandler): () => void {
  closeHandlers.add(handler);
  return () => closeHandlers.delete(handler);
}

// ───────────────────────── The dispatcher receiver ─────────────────────────

const dispatcher: IStreamHubClient = {
  async onPong(): Promise<void> {
    // No subscribers today; left implemented for the typed-receiver contract.
  },
  async onToken(content: string): Promise<void> {
    fanOut(chatTokenHandlers, content);
  },
  async onComplete(): Promise<void> {
    fanOut(chatCompleteHandlers, undefined);
  },
  async onError(message: string): Promise<void> {
    fanOut(chatErrorHandlers, message);
  },
  async onModelDownloadStarted(event: ModelDownloadStarted): Promise<void> {
    fanOut(dlStartedHandlers, event);
  },
  async onModelDownloadProgress(event: ModelDownloadProgress): Promise<void> {
    fanOut(dlProgressHandlers, event);
  },
  async onModelDownloadComplete(event: ModelDownloadComplete): Promise<void> {
    fanOut(dlCompleteHandlers, event);
  },
  async onModelDownloadFailed(event: ModelDownloadFailed): Promise<void> {
    fanOut(dlFailedHandlers, event);
  },
};

// ───────────────────────── Connection lifecycle ─────────────────────────

export async function acquireStreamHub(): Promise<IStreamHub> {
  if (!connection) {
    connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/stream')
      .withAutomaticReconnect()
      .build();
    getReceiverRegister('IStreamHubClient').register(connection, dispatcher);
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
    proxy = getHubProxyFactory('IStreamHub').createHubProxy(connection);
  }
  return proxy;
}

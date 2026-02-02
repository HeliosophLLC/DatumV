import * as signalR from '@microsoft/signalr';
import {
  getHubProxyFactory,
  getReceiverRegister,
} from './generated/hubs/TypedSignalR.Client';
import type {
  IStreamHub,
  IStreamHubClient,
} from './generated/hubs/TypedSignalR.Client/DatumIngest.Web.Hubs';

// Singleton HubConnection + proxy. The receiver is registered once for the
// app's lifetime (it routes server-pushed events into conversationState).
// Connection is built and started lazily on first acquireHub call so the
// SPA's first paint isn't gated on hub readiness.
let connection: signalR.HubConnection | null = null;
let connectPromise: Promise<void> | null = null;
let proxy: IStreamHub | null = null;

type CloseHandler = (err?: Error) => void;
const closeHandlers: CloseHandler[] = [];

// Register a listener for connection-closed events. SignalR's
// withAutomaticReconnect handles transient drops by silently reconnecting,
// but a permanent close (server gone, max retries exhausted) needs surface
// in app state so the UI doesn't stay stuck in 'streaming'.
export function onConnectionClosed(handler: CloseHandler): void {
  closeHandlers.push(handler);
}

export async function acquireStreamHub(receiver: IStreamHubClient): Promise<IStreamHub> {
  if (!connection) {
    connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/stream')
      .withAutomaticReconnect()
      .build();
    getReceiverRegister('IStreamHubClient').register(connection, receiver);
    connection.onclose((err) => {
      for (const handler of closeHandlers) {
        try {
          handler(err);
        } catch {
          // Handler bugs shouldn't break sibling handlers or future calls.
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

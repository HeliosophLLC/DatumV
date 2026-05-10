import * as signalR from '@microsoft/signalr';
import {
  getHubProxyFactory,
  getReceiverRegister,
} from './generated/hubs/TypedSignalR.Client';
import type {
  IStreamHub,
  IStreamHubClient,
} from './generated/hubs/TypedSignalR.Client/DatumIngest.Web.Hubs';
// The codegen emits these as `*Dto` types (matching the C# DTO names in
// DatumIngest.Web.Hubs/ModelDownloadDtos.cs). Alias them to the
// suffix-free names that downstream consumers already use, so the file
// rename + DTO suffix don't leak past this boundary.
import type {
  ModelDownloadStartedDto as ModelDownloadStarted,
  ModelDownloadProgressDto as ModelDownloadProgress,
  ModelDownloadCompleteDto as ModelDownloadComplete,
  ModelInstallingDto as ModelInstalling,
  ModelInstalledDto as ModelInstalled,
  ModelDownloadFailedDto as ModelDownloadFailed,
  UvDownloadStartedDto as UvDownloadStarted,
  UvDownloadProgressDto as UvDownloadProgress,
  UvDownloadCompleteDto as UvDownloadComplete,
  PythonInstallStartedDto as PythonInstallStarted,
  PythonInstallProgressDto as PythonInstallProgress,
  PythonInstallCompleteDto as PythonInstallComplete,
  VenvInstallStartedDto as VenvInstallStarted,
  VenvInstallProgressDto as VenvInstallProgress,
  VenvInstallCompleteDto as VenvInstallComplete,
  PythonEnvironmentFailedDto as PythonEnvironmentFailed,
  DatasetDownloadStartedDto as DatasetDownloadStarted,
  DatasetDownloadProgressDto as DatasetDownloadProgress,
  DatasetDownloadCompleteDto as DatasetDownloadComplete,
  DatasetIngestingDto as DatasetIngesting,
  DatasetIngestProgressDto as DatasetIngestProgress,
  DatasetTableIngestedDto as DatasetTableIngested,
  DatasetInstalledDto as DatasetInstalled,
  DatasetDownloadFailedDto as DatasetDownloadFailed,
} from './generated/hubs/DatumIngest.Web.Hubs';
export type {
  ModelDownloadStarted,
  ModelDownloadProgress,
  ModelDownloadComplete,
  ModelInstalling,
  ModelInstalled,
  ModelDownloadFailed,
  UvDownloadStarted,
  UvDownloadProgress,
  UvDownloadComplete,
  PythonInstallStarted,
  PythonInstallProgress,
  PythonInstallComplete,
  VenvInstallStarted,
  VenvInstallProgress,
  VenvInstallComplete,
  PythonEnvironmentFailed,
  DatasetDownloadStarted,
  DatasetDownloadProgress,
  DatasetDownloadComplete,
  DatasetIngesting,
  DatasetIngestProgress,
  DatasetTableIngested,
  DatasetInstalled,
  DatasetDownloadFailed,
};

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
const dlInstallingHandlers: Set<Handler<ModelInstalling>> = new Set();
const dlInstalledHandlers: Set<Handler<ModelInstalled>> = new Set();
const dlFailedHandlers: Set<Handler<ModelDownloadFailed>> = new Set();

const uvStartedHandlers: Set<Handler<UvDownloadStarted>> = new Set();
const uvProgressHandlers: Set<Handler<UvDownloadProgress>> = new Set();
const uvCompleteHandlers: Set<Handler<UvDownloadComplete>> = new Set();
const pyStartedHandlers: Set<Handler<PythonInstallStarted>> = new Set();
const pyProgressHandlers: Set<Handler<PythonInstallProgress>> = new Set();
const pyCompleteHandlers: Set<Handler<PythonInstallComplete>> = new Set();
const venvStartedHandlers: Set<Handler<VenvInstallStarted>> = new Set();
const venvProgressHandlers: Set<Handler<VenvInstallProgress>> = new Set();
const venvCompleteHandlers: Set<Handler<VenvInstallComplete>> = new Set();
const pythonFailedHandlers: Set<Handler<PythonEnvironmentFailed>> = new Set();

const dsStartedHandlers: Set<Handler<DatasetDownloadStarted>> = new Set();
const dsProgressHandlers: Set<Handler<DatasetDownloadProgress>> = new Set();
const dsCompleteHandlers: Set<Handler<DatasetDownloadComplete>> = new Set();
const dsIngestingHandlers: Set<Handler<DatasetIngesting>> = new Set();
const dsIngestProgressHandlers: Set<Handler<DatasetIngestProgress>> = new Set();
const dsTableIngestedHandlers: Set<Handler<DatasetTableIngested>> = new Set();
const dsInstalledHandlers: Set<Handler<DatasetInstalled>> = new Set();
const dsFailedHandlers: Set<Handler<DatasetDownloadFailed>> = new Set();

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
export const onModelInstalling = (handler: Handler<ModelInstalling>) =>
  subscribe(dlInstallingHandlers, handler);
export const onModelInstalled = (handler: Handler<ModelInstalled>) =>
  subscribe(dlInstalledHandlers, handler);
export const onModelDownloadFailed = (handler: Handler<ModelDownloadFailed>) =>
  subscribe(dlFailedHandlers, handler);

export const onUvDownloadStarted = (handler: Handler<UvDownloadStarted>) =>
  subscribe(uvStartedHandlers, handler);
export const onUvDownloadProgress = (handler: Handler<UvDownloadProgress>) =>
  subscribe(uvProgressHandlers, handler);
export const onUvDownloadComplete = (handler: Handler<UvDownloadComplete>) =>
  subscribe(uvCompleteHandlers, handler);
export const onPythonInstallStarted = (handler: Handler<PythonInstallStarted>) =>
  subscribe(pyStartedHandlers, handler);
export const onPythonInstallProgress = (handler: Handler<PythonInstallProgress>) =>
  subscribe(pyProgressHandlers, handler);
export const onPythonInstallComplete = (handler: Handler<PythonInstallComplete>) =>
  subscribe(pyCompleteHandlers, handler);
export const onVenvInstallStarted = (handler: Handler<VenvInstallStarted>) =>
  subscribe(venvStartedHandlers, handler);
export const onVenvInstallProgress = (handler: Handler<VenvInstallProgress>) =>
  subscribe(venvProgressHandlers, handler);
export const onVenvInstallComplete = (handler: Handler<VenvInstallComplete>) =>
  subscribe(venvCompleteHandlers, handler);
export const onPythonEnvironmentFailed = (handler: Handler<PythonEnvironmentFailed>) =>
  subscribe(pythonFailedHandlers, handler);

export const onDatasetDownloadStarted = (handler: Handler<DatasetDownloadStarted>) =>
  subscribe(dsStartedHandlers, handler);
export const onDatasetDownloadProgress = (handler: Handler<DatasetDownloadProgress>) =>
  subscribe(dsProgressHandlers, handler);
export const onDatasetDownloadComplete = (handler: Handler<DatasetDownloadComplete>) =>
  subscribe(dsCompleteHandlers, handler);
export const onDatasetIngesting = (handler: Handler<DatasetIngesting>) =>
  subscribe(dsIngestingHandlers, handler);
export const onDatasetIngestProgress = (handler: Handler<DatasetIngestProgress>) =>
  subscribe(dsIngestProgressHandlers, handler);
export const onDatasetTableIngested = (handler: Handler<DatasetTableIngested>) =>
  subscribe(dsTableIngestedHandlers, handler);
export const onDatasetInstalled = (handler: Handler<DatasetInstalled>) =>
  subscribe(dsInstalledHandlers, handler);
export const onDatasetDownloadFailed = (handler: Handler<DatasetDownloadFailed>) =>
  subscribe(dsFailedHandlers, handler);

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
  async onModelInstalling(event: ModelInstalling): Promise<void> {
    fanOut(dlInstallingHandlers, event);
  },
  async onModelInstalled(event: ModelInstalled): Promise<void> {
    fanOut(dlInstalledHandlers, event);
  },
  async onModelDownloadFailed(event: ModelDownloadFailed): Promise<void> {
    fanOut(dlFailedHandlers, event);
  },
  async onUvDownloadStarted(event: UvDownloadStarted): Promise<void> {
    fanOut(uvStartedHandlers, event);
  },
  async onUvDownloadProgress(event: UvDownloadProgress): Promise<void> {
    fanOut(uvProgressHandlers, event);
  },
  async onUvDownloadComplete(event: UvDownloadComplete): Promise<void> {
    fanOut(uvCompleteHandlers, event);
  },
  async onPythonInstallStarted(event: PythonInstallStarted): Promise<void> {
    fanOut(pyStartedHandlers, event);
  },
  async onPythonInstallProgress(event: PythonInstallProgress): Promise<void> {
    fanOut(pyProgressHandlers, event);
  },
  async onPythonInstallComplete(event: PythonInstallComplete): Promise<void> {
    fanOut(pyCompleteHandlers, event);
  },
  async onVenvInstallStarted(event: VenvInstallStarted): Promise<void> {
    fanOut(venvStartedHandlers, event);
  },
  async onVenvInstallProgress(event: VenvInstallProgress): Promise<void> {
    fanOut(venvProgressHandlers, event);
  },
  async onVenvInstallComplete(event: VenvInstallComplete): Promise<void> {
    fanOut(venvCompleteHandlers, event);
  },
  async onPythonEnvironmentFailed(event: PythonEnvironmentFailed): Promise<void> {
    fanOut(pythonFailedHandlers, event);
  },
  async onDatasetDownloadStarted(event: DatasetDownloadStarted): Promise<void> {
    fanOut(dsStartedHandlers, event);
  },
  async onDatasetDownloadProgress(event: DatasetDownloadProgress): Promise<void> {
    fanOut(dsProgressHandlers, event);
  },
  async onDatasetDownloadComplete(event: DatasetDownloadComplete): Promise<void> {
    fanOut(dsCompleteHandlers, event);
  },
  async onDatasetIngesting(event: DatasetIngesting): Promise<void> {
    fanOut(dsIngestingHandlers, event);
  },
  async onDatasetIngestProgress(event: DatasetIngestProgress): Promise<void> {
    fanOut(dsIngestProgressHandlers, event);
  },
  async onDatasetTableIngested(event: DatasetTableIngested): Promise<void> {
    fanOut(dsTableIngestedHandlers, event);
  },
  async onDatasetInstalled(event: DatasetInstalled): Promise<void> {
    fanOut(dsInstalledHandlers, event);
  },
  async onDatasetDownloadFailed(event: DatasetDownloadFailed): Promise<void> {
    fanOut(dsFailedHandlers, event);
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

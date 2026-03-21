import {
  FunctionCatalogClient,
  HealthClient,
  LanguageClient,
  ModelCatalogClient,
  SchemaCatalogClient,
  SettingsClient,
} from './generated/openapi-client';

// Wrap window.fetch so every NSwag-generated client sends cookies. The
// generated clients are credentials-agnostic — this is the single place that
// decides "yes, send cookies on every request." Same wrapper will compose
// future cross-cutting concerns (auth header, error mapping, telemetry).
function createCredentialedHttp() {
  return {
    fetch: (url: RequestInfo, init?: RequestInit): Promise<Response> =>
      window.fetch(url, { ...init, credentials: 'include' }),
  };
}

// Composed API surface. Add new clients here as controllers land in
// DatumIngest.Web. Default baseUrl is empty (relative URLs → current origin,
// which is Vite in dev — proxying /api and /hubs to Kestrel — and Kestrel
// directly in prod).
export function createApi(baseUrl = '') {
  const http = createCredentialedHttp();
  return {
    health: new HealthClient(baseUrl, http),
    settings: new SettingsClient(baseUrl, http),
    modelCatalog: new ModelCatalogClient(baseUrl, http),
    language: new LanguageClient(baseUrl, http),
    functionCatalog: new FunctionCatalogClient(baseUrl, http),
    schemaCatalog: new SchemaCatalogClient(baseUrl, http),
  };
}

export const api = createApi();

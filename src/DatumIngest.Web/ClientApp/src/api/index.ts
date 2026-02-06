import {
  HealthClient,
  ModelCatalogClient,
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
// which is what Photino's in-process Kestrel always serves).
export function createApi(baseUrl = '') {
  const http = createCredentialedHttp();
  return {
    health: new HealthClient(baseUrl, http),
    settings: new SettingsClient(baseUrl, http),
    modelCatalog: new ModelCatalogClient(baseUrl, http),
  };
}

export const api = createApi();

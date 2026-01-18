// Renders a parsed JSON value as a collapsible DOM tree. Used by the
// JSON cell renderer in place of a flat pretty-printed string.
//
// Shape rules:
//   - Primitives (string / number / boolean / null) render inline with
//     a syntax-coloured span.
//   - Empty objects render as `{}`, empty arrays as `[]` (no toggle).
//   - Non-empty objects/arrays render as <details>: a summary line
//     showing `{` plus a count preview ("3 keys" / "5 items"), and a
//     children block with each entry on its own row. Top level opens
//     by default; nested levels stay collapsed so deep LLM-style
//     payloads don't dominate the cell.

export type JsonValue =
  | null
  | string
  | number
  | boolean
  | JsonValue[]
  | { [key: string]: JsonValue };

export function renderJsonNode(value: JsonValue, depth = 0): HTMLElement {
  if (value === null) {
    const span = document.createElement('span');
    span.className = 'json-null';
    span.textContent = 'null';
    return span;
  }
  const t = typeof value;
  if (t === 'string') {
    const span = document.createElement('span');
    span.className = 'json-string';
    span.textContent = JSON.stringify(value);
    return span;
  }
  if (t === 'number') {
    const span = document.createElement('span');
    span.className = 'json-number';
    span.textContent = String(value);
    return span;
  }
  if (t === 'boolean') {
    const span = document.createElement('span');
    span.className = 'json-boolean';
    span.textContent = value ? 'true' : 'false';
    return span;
  }
  if (Array.isArray(value)) {
    return renderJsonArray(value, depth);
  }
  if (t === 'object') {
    return renderJsonObject(value as { [key: string]: JsonValue }, depth);
  }
  // Fallback for exotic types (BigInt, etc.) — shouldn't appear from
  // JSON.parse but cover the case so the renderer never throws.
  const span = document.createElement('span');
  span.textContent = String(value);
  return span;
}

export function renderJsonObject(
  obj: { [key: string]: JsonValue },
  depth: number,
): HTMLElement {
  const keys = Object.keys(obj);
  if (keys.length === 0) {
    const span = document.createElement('span');
    span.className = 'json-bracket';
    span.textContent = '{}';
    return span;
  }
  const details = document.createElement('details');
  details.open = depth === 0;
  const summary = document.createElement('summary');
  const opener = document.createElement('span');
  opener.className = 'json-bracket';
  opener.textContent = '{';
  summary.appendChild(opener);
  const preview = document.createElement('span');
  preview.className = 'json-summary-preview';
  preview.textContent = `${keys.length} ${keys.length === 1 ? 'key' : 'keys'}`;
  summary.appendChild(preview);
  details.appendChild(summary);

  const children = document.createElement('div');
  children.className = 'json-children';
  keys.forEach((key, i) => {
    const row = document.createElement('span');
    row.className = 'json-row';
    const k = document.createElement('span');
    k.className = 'json-key';
    k.textContent = JSON.stringify(key);
    row.appendChild(k);
    row.appendChild(document.createTextNode(': '));
    row.appendChild(renderJsonNode(obj[key], depth + 1));
    if (i < keys.length - 1) {
      const comma = document.createElement('span');
      comma.className = 'json-comma';
      comma.textContent = ',';
      row.appendChild(comma);
    }
    children.appendChild(row);
  });
  details.appendChild(children);

  const closer = document.createElement('span');
  closer.className = 'json-bracket';
  closer.textContent = '}';
  details.appendChild(closer);
  return details;
}

export function renderJsonArray(arr: JsonValue[], depth: number): HTMLElement {
  if (arr.length === 0) {
    const span = document.createElement('span');
    span.className = 'json-bracket';
    span.textContent = '[]';
    return span;
  }
  const details = document.createElement('details');
  details.open = depth === 0;
  const summary = document.createElement('summary');
  const opener = document.createElement('span');
  opener.className = 'json-bracket';
  opener.textContent = '[';
  summary.appendChild(opener);
  const preview = document.createElement('span');
  preview.className = 'json-summary-preview';
  preview.textContent = `${arr.length} ${arr.length === 1 ? 'item' : 'items'}`;
  summary.appendChild(preview);
  details.appendChild(summary);

  const children = document.createElement('div');
  children.className = 'json-children';
  arr.forEach((item, i) => {
    const row = document.createElement('span');
    row.className = 'json-row';
    row.appendChild(renderJsonNode(item, depth + 1));
    if (i < arr.length - 1) {
      const comma = document.createElement('span');
      comma.className = 'json-comma';
      comma.textContent = ',';
      row.appendChild(comma);
    }
    children.appendChild(row);
  });
  details.appendChild(children);

  const closer = document.createElement('span');
  closer.className = 'json-bracket';
  closer.textContent = ']';
  details.appendChild(closer);
  return details;
}

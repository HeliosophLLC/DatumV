// SQL template builders. Pure string transforms — no DOM, no fetch.
// Inputs come from system_udfs / system_procedures / built-in function
// manifests; outputs are the scaffolded SQL we drop into a fresh tab.

export interface ParsedParam {
  name: string;
  type: string;
}

export interface UdfRow {
  name: string;
  body_kind?: string;
  body?: string;
  parameters?: string;
  return_type?: string;
}

export interface ProcedureRow {
  name: string;
  source_text?: string;
}

export interface BuiltinFunction {
  name: string;
  parameters?: ParsedParam[];
  isAggregate?: boolean;
  isWindow?: boolean;
}

// Splits on `delim` ignoring delimiters inside parens / brackets / braces
// and quoted strings. Used because parameter defaults may contain commas.
export function splitTopLevel(s: string, delim: string): string[] {
  const out: string[] = [];
  let depth = 0;
  let buf = '';
  let inStr: string | null = null;
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (inStr) {
      buf += c;
      if (c === inStr && s[i - 1] !== '\\') inStr = null;
      continue;
    }
    if (c === "'" || c === '"' || c === '`') { inStr = c; buf += c; continue; }
    if (c === '(' || c === '[' || c === '{') depth++;
    if (c === ')' || c === ']' || c === '}') depth--;
    if (depth === 0 && c === delim) { out.push(buf); buf = ''; continue; }
    buf += c;
  }
  if (buf.length) out.push(buf);
  return out;
}

// Parses a parameter string from system_udfs / system_procedures.
// Format mirrors what UdfsTableProvider / ProceduresTableProvider emit:
//   "@a INT32, @b STRING IS NOT NULL, @c INT32 = 0"
// We pull each parameter's name and type for DECLARE scaffolding.
export function parseParameterList(s: string | undefined | null): ParsedParam[] {
  if (!s) return [];
  const parts = splitTopLevel(s, ',');
  const result: ParsedParam[] = [];
  for (const p of parts) {
    const m = p.trim().match(/^@(\w+)\s+(\w+)/);
    if (m) result.push({ name: m[1], type: m[2] });
  }
  return result;
}

// Builds:
//   DECLARE @a INT32
//   DECLARE @b STRING
//   EXEC udf.name(@a, @b)
// for UDFs/procedures. Caller fills in values before running.
export function buildExecuteTemplate(
  prefix: string,
  name: string,
  parameterList: string | undefined | null,
): string {
  const params = parseParameterList(parameterList);
  if (params.length === 0) {
    return `EXEC ${prefix}.${name}()`;
  }
  const declares = params.map((p) => `DECLARE @${p.name} ${p.type}`).join('\n');
  const args = params.map((p) => `@${p.name}`).join(', ');
  return `${declares}\n\nEXEC ${prefix}.${name}(${args})`;
}

// Macro UDFs don't persist original source — recompose from parameters,
// return type, and the formatted body expression. Procedural UDFs do
// persist their full source_text in the body column, so we rewrite the
// header to `CREATE OR ALTER FUNCTION` and leave the BEGIN…END body alone.
export function buildModifyTemplateFromUdfRow(udf: UdfRow): string {
  if ((udf.body_kind || 'macro').toLowerCase() === 'procedural') {
    let src = udf.body || '';
    src = src.replace(
      /^\s*CREATE\s+(OR\s+REPLACE\s+|OR\s+ALTER\s+)?(PURE\s+)?FUNCTION\b/i,
      (_match, _orPrefix, pureKw) =>
        `CREATE OR ALTER ${pureKw ? 'PURE ' : ''}FUNCTION`);
    return src || `CREATE OR ALTER FUNCTION ${udf.name}() RETURNS STRING BEGIN\n  RETURN ''\nEND`;
  }
  const params = udf.parameters || '';
  const returns = udf.return_type ? ` RETURNS ${udf.return_type}` : '';
  const body = udf.body || 'NULL';
  return `CREATE OR ALTER FUNCTION ${udf.name}(${params})${returns} AS\n  ${body}`;
}

// Procedures persist the verbatim source text — modify by rewriting
// CREATE [OR REPLACE] PROCEDURE → CREATE OR ALTER PROCEDURE.
export function buildModifyTemplateFromProcedureRow(proc: ProcedureRow): string {
  let src = proc.source_text || '';
  src = src.replace(
    /^\s*CREATE\s+(OR\s+REPLACE\s+)?PROCEDURE\b/i,
    'CREATE OR ALTER PROCEDURE');
  return src || `CREATE OR ALTER PROCEDURE ${proc.name}() AS BEGIN\n  -- body\nEND`;
}

// For built-in functions: scaffold a SELECT that calls the function with
// placeholder DECLAREs typed by the manifest's parameter list. Aggregate
// and window functions take a column expression — we emit a comment hint
// instead of a generic call site.
export function buildBuiltinExecuteTemplate(fn: BuiltinFunction): string {
  const params = fn.parameters || [];
  if (fn.isAggregate || fn.isWindow) {
    return `-- ${fn.name} is ${fn.isAggregate ? 'an aggregate' : 'a window'} function.\n` +
      `-- Use it inside a SELECT against a table:\n` +
      `--   SELECT ${fn.name}(some_column) FROM your_table${fn.isWindow ? ' OVER (...)' : ''}`;
  }
  if (params.length === 0) {
    return `SELECT ${fn.name}()`;
  }
  const declares = params.map((p) => `DECLARE @${p.name} ${p.type}`).join('\n');
  const args = params.map((p) => `@${p.name}`).join(', ');
  return `${declares}\n\nSELECT ${fn.name}(${args})`;
}

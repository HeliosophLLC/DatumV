import { useEffect } from 'react';
import { useSnapshot } from 'valtio';
import { useTranslation } from 'react-i18next';
import { Loader2 } from 'lucide-react';
import {
  functionCatalogState,
  loadProcedures,
  loadUdfs,
} from '@/state/functionCatalog';
import type {
  ProcedureDto,
  ScalarFunctionParameterDto,
  UdfDto,
} from '@/api/generated/openapi-client';

// Two collapsible sections: Procedures + UDFs. Names + parameter lists
// only; clicking a row is reserved for a future "preview source" or
// "insert into editor" gesture (no consumer wired today, so the row is
// presentational).
export function ProceduresPanel() {
  const { t } = useTranslation('procedures');
  const { procedureStatus, procedures, procedureError, udfStatus, udfs, udfError } =
    useSnapshot(functionCatalogState);

  useEffect(() => {
    void loadProcedures();
    void loadUdfs();
  }, []);

  return (
    <div className="h-full overflow-y-auto py-1 text-xs">
      <Section
        title={t('procedures')}
        status={procedureStatus}
        error={procedureError}
        empty={procedures.length === 0}
        emptyLabel={t('noProcedures')}
      >
        {procedures.map((p) => (
          <ProcedureRow
            key={`${p.schema ?? ''}.${p.name ?? ''}`}
            entry={p as ProcedureDto}
          />
        ))}
      </Section>
      <Section
        title={t('udfs')}
        status={udfStatus}
        error={udfError}
        empty={udfs.length === 0}
        emptyLabel={t('noUdfs')}
      >
        {udfs.map((u) => (
          <UdfRow
            key={`${u.schema ?? ''}.${u.name ?? ''}`}
            entry={u as UdfDto}
          />
        ))}
      </Section>
    </div>
  );
}

function Section({
  title,
  status,
  error,
  empty,
  emptyLabel,
  children,
}: {
  title: string;
  status: 'idle' | 'loading' | 'ready' | 'error';
  error: string | null;
  empty: boolean;
  emptyLabel: string;
  children: React.ReactNode;
}) {
  const { t } = useTranslation('procedures');
  return (
    <div className="mb-3">
      <h3 className="text-muted-foreground border-b px-3 pb-1 text-[10px] tracking-wide uppercase">
        {title}
      </h3>
      {status === 'loading' && (
        <div className="text-muted-foreground flex items-center gap-2 px-3 py-2">
          <Loader2 className="size-3.5 animate-spin" /> {t('loading')}
        </div>
      )}
      {status === 'error' && (
        <p className="text-destructive px-3 py-2 break-words">{error}</p>
      )}
      {status === 'ready' && empty && (
        <p className="text-muted-foreground px-3 py-2 italic">{emptyLabel}</p>
      )}
      <ul>{children}</ul>
    </div>
  );
}

function ProcedureRow({ entry }: { entry: ProcedureDto }) {
  return (
    <li className="hover:bg-primary/10 px-3 py-1 font-mono transition-colors">
      <div className="truncate">
        <span className="text-muted-foreground">{entry.schema ?? ''}.</span>
        {entry.name ?? ''}
        <span className="text-muted-foreground">{paramSummary(entry.parameters)}</span>
      </div>
    </li>
  );
}

function UdfRow({ entry }: { entry: UdfDto }) {
  return (
    <li className="hover:bg-primary/10 px-3 py-1 font-mono transition-colors">
      <div className="truncate">
        <span className="text-muted-foreground">{entry.schema ?? ''}.</span>
        {entry.name ?? ''}
        <span className="text-muted-foreground">{paramSummary(entry.parameters)}</span>
        {entry.returnType && (
          <span className="text-muted-foreground ml-2">→ {entry.returnType}</span>
        )}
      </div>
      <div className="text-muted-foreground text-[10px] uppercase">
        {entry.bodyKind}
        {entry.isPure && ' · pure'}
      </div>
    </li>
  );
}

function paramSummary(params: readonly ScalarFunctionParameterDto[] | undefined): string {
  if (!params || params.length === 0) return '()';
  return `(${params.map((p) => p.name ?? '').join(', ')})`;
}

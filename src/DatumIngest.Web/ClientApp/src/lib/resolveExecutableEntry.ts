import type {
  ModelDto,
  ScalarFunctionDto,
  ScalarFunctionParameterDto,
  ScalarFunctionReturnTypeDto,
  ScalarFunctionSignatureDto,
  UdfDto,
} from '@/api/generated/openapi-client';
import { functionCatalogState } from '@/state/functionCatalog';
import type {
  FunctionFormSelection,
  FunctionFormSource,
} from '@/state/functionForm';

// Normalises the three picker sources (scalar / udf / model) into a
// single shape the rest of the form code consumes uniformly. The form
// only needs to know "what's the schema-qualified name, the parameter
// list, the variadic spec if any, and the return type"; the source-
// specific metadata (BodyKind for UDFs, ImplementsTask + UsingPath for
// models) is preserved on the entry for the form header to surface.

export interface ResolvedExecutable {
  source: FunctionFormSource;
  schema: string;
  name: string;
  description: string | null;
  /** Synthesised single-signature view — UDFs/models have one, scalars yield their picked variant. */
  variant: ScalarFunctionSignatureDto;
  /** The full ScalarFunctionDto when source === 'scalar'. Lets the form keep its overload picker. */
  scalar: ScalarFunctionDto | null;
  /** UDF metadata when source === 'udf'. */
  udf: UdfDto | null;
  /** Model metadata when source === 'model'. */
  model: ModelDto | null;
}

/**
 * Looks up the picked entry in the right catalog list and returns a
 * normalised view. Returns `null` when the selection doesn't resolve
 * (function was unregistered between picks, catalog still loading,
 * etc.) so callers can bail safely rather than render against stale
 * state.
 */
export function resolveExecutable(
  selection: FunctionFormSelection,
): ResolvedExecutable | null {
  if (selection.source === 'scalar') {
    const fn = (functionCatalogState.scalars as readonly ScalarFunctionDto[]).find(
      (f) => f.schema === selection.schema && f.name === selection.name,
    );
    if (!fn) return null;
    const variant = fn.signatures?.[selection.variantIndex];
    if (!variant) return null;
    return {
      source: 'scalar',
      schema: fn.schema ?? '',
      name: fn.name ?? '',
      description: fn.description ?? null,
      variant: variant as ScalarFunctionSignatureDto,
      scalar: fn as ScalarFunctionDto,
      udf: null,
      model: null,
    };
  }

  if (selection.source === 'udf') {
    const udf = (functionCatalogState.udfs as readonly UdfDto[]).find(
      (u) => u.schema === selection.schema && u.name === selection.name,
    );
    if (!udf) return null;
    return {
      source: 'udf',
      schema: udf.schema ?? '',
      name: udf.name ?? '',
      // UDFs don't carry a separate description field; show the source
      // text snippet on the form body header instead.
      description: null,
      variant: variantFromParams(
        udf.parameters ?? [],
        returnDescriptorFromString(udf.returnType ?? null),
      ),
      scalar: null,
      udf: udf as UdfDto,
      model: null,
    };
  }

  // source === 'model'
  const model = (functionCatalogState.models as readonly ModelDto[]).find(
    (m) => m.schema === selection.schema && m.name === selection.name,
  );
  if (!model) return null;
  return {
    source: 'model',
    schema: model.schema ?? '',
    name: model.name ?? '',
    description: null,
    variant: variantFromParams(
      model.parameters ?? [],
      returnDescriptorFromString(model.returnType ?? null),
    ),
    scalar: null,
    udf: null,
    model: model as ModelDto,
  };
}

/**
 * Synthesises a `ScalarFunctionSignatureDto` from a flat parameter list +
 * return-type descriptor. UDFs and models always have exactly one
 * signature and no variadic in v1, so the wrapper is trivial.
 */
function variantFromParams(
  parameters: ScalarFunctionParameterDto[] | readonly ScalarFunctionParameterDto[],
  returnType: ScalarFunctionReturnTypeDto,
): ScalarFunctionSignatureDto {
  return {
    parameters: parameters as ScalarFunctionParameterDto[],
    variadic: undefined,
    returnType,
  };
}

/**
 * Fakes a return-type descriptor from a verbatim type name (UDF/model
 * `ReturnType`). Since the engine-side type isn't a `DataKind` enum value
 * here, `staticHint` stays null — the form's single-cell rendering
 * branches on the actual cell kind at run time anyway.
 */
function returnDescriptorFromString(
  typeName: string | null,
): ScalarFunctionReturnTypeDto {
  return {
    description: typeName ?? '',
    staticHint: undefined,
    producesArray: false,
  };
}

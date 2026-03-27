import type { JsonCell } from '@/state/execution';

/**
 * Decodes the bytes payload of a JsonCell into an ArrayBuffer.
 *
 * Cells with `encoding: "gzip"` (the PointCloud / Mesh transports) get
 * piped through the browser-native `DecompressionStream` API. Cells with
 * no encoding (raw base64) are decoded straight to bytes.
 *
 * Shared between PointCloudViewer and MeshViewer since the decode path
 * is identical — both formats are version-tagged byte blobs with header
 * + payload that the viewer parses client-side.
 */
export async function decodeCellBytes(cell: JsonCell): Promise<ArrayBuffer> {
  if (cell.dataB64 === undefined) {
    throw new Error('Cell missing dataB64 payload');
  }

  const compressed = base64ToBytes(cell.dataB64);

  if (cell.encoding === 'gzip') {
    if (typeof DecompressionStream === 'undefined') {
      throw new Error(
        'This browser does not support DecompressionStream '
        + '(Chrome 80+, Firefox 113+, Safari 16.4+)',
      );
    }
    // Wrap the Uint8Array in a Blob so the body conforms to BodyInit
    // (TS's typing of `new Response(...)` rejects bare Uint8Array even
    // though runtime accepts it). Then stream through DecompressionStream
    // and collect to a single ArrayBuffer.
    const stream = new Response(new Blob([compressed as BlobPart])).body!.pipeThrough(
      new DecompressionStream('gzip'),
    );
    return await new Response(stream).arrayBuffer();
  }

  return compressed.buffer.slice(
    compressed.byteOffset,
    compressed.byteOffset + compressed.byteLength,
  ) as ArrayBuffer;
}

function base64ToBytes(b64: string): Uint8Array {
  const binary = atob(b64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}

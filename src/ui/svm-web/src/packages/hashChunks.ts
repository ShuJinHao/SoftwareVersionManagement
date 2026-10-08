import { createSHA256 } from 'hash-wasm'

export const HASH_CHUNK_BYTES = 1024 * 1024
export async function hashChunks(blob: Blob, progress: (bytes: number) => void, signal?: AbortSignal): Promise<string> {
  const hash = await createSHA256(); hash.init()
  for (let offset = 0; offset < blob.size; offset += HASH_CHUNK_BYTES) {
    signal?.throwIfAborted()
    hash.update(new Uint8Array(await blob.slice(offset, offset + HASH_CHUNK_BYTES).arrayBuffer()))
    progress(Math.min(offset + HASH_CHUNK_BYTES, blob.size))
    await new Promise<void>(resolve => setTimeout(resolve, 0))
  }
  signal?.throwIfAborted()
  return hash.digest('hex')
}

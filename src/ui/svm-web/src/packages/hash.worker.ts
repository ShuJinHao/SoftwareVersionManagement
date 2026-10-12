import { hashChunks } from './hashChunks'

self.onmessage = async (event: MessageEvent<{ file: File }>) => {
  try {
    const digest = await hashChunks(event.data.file, bytes => self.postMessage({ kind: 'progress', bytes }))
    self.postMessage({ kind: 'complete', digest })
  } catch { self.postMessage({ kind: 'error' }) }
}

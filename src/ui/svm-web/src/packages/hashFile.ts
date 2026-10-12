export function hashFile(file: File, progress: (bytes: number) => void, signal: AbortSignal): Promise<string> {
  return new Promise((resolve, reject) => {
    if (signal.aborted) { reject(new DOMException('摘要计算已取消', 'AbortError')); return }
    const worker = new Worker(new URL('./hash.worker.ts', import.meta.url), { type: 'module' })
    const close = () => { signal.removeEventListener('abort', cancel); worker.terminate() }
    const cancel = () => { close(); reject(new DOMException('摘要计算已取消', 'AbortError')) }
    signal.addEventListener('abort', cancel, { once: true })
    worker.onmessage = (event: MessageEvent<{ kind: string; bytes?: number; digest?: string }>) => {
      if (event.data.kind === 'progress') progress(event.data.bytes!)
      else if (event.data.kind === 'complete' && /^[a-f0-9]{64}$/.test(event.data.digest ?? '')) { close(); resolve(event.data.digest!) }
      else { close(); reject(new Error('摘要计算失败。')) }
    }
    worker.onerror = () => { close(); reject(new Error('摘要计算失败。')) }
    worker.postMessage({ file })
  })
}

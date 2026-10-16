// Read ownership is independent of transport cancellation: even a transport that
// finishes after abort cannot apply facts from a previous object or permission.
export function createReadScope() {
  let generation = 0
  const active = new Set<AbortController>()
  return {
    invalidate() {
      generation++
      for (const controller of active) controller.abort()
      active.clear()
    },
    capture() {
      const owner = generation
      const controller = new AbortController()
      active.add(controller)
      return {
        signal: controller.signal,
        current: () => owner === generation && !controller.signal.aborted,
        finish: () => active.delete(controller),
      }
    },
  }
}

export class GenerationFence {
  private value = 0

  capture() { return this.value }
  invalidate() { this.value += 1 }
  isCurrent(value: number) { return value === this.value }

  async runLatest<T>(operation: () => Promise<T>): Promise<{ current: boolean; value?: T; error?: unknown }> {
    this.invalidate()
    const generation = this.capture()
    try {
      const value = await operation()
      return { current: this.isCurrent(generation), value }
    }
    catch (error) { return { current: this.isCurrent(generation), error } }
  }
}
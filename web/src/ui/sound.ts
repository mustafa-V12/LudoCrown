// Plays the app's sound effects (same files as src/LudoCrown.Mobile/Resources/Raw) through Web Audio.
// iOS only allows audio after a user gesture, so the context is created/resumed on the first tap.
export type SoundEffect = 'dice' | 'move' | 'capture' | 'enter' | 'finish' | 'win' | 'lose' | 'turn' | 'tap';

const SOUND_KEY = 'ludocrown.sound';

export class Sound {
  private ctx: AudioContext | null = null;
  private readonly buffers = new Map<SoundEffect, AudioBuffer>();
  private loading: Promise<void> | null = null;
  enabled: boolean;

  constructor(private readonly base: string) {
    let stored: string | null = null;
    try {
      stored = localStorage.getItem(SOUND_KEY);
    } catch {
      /* ignore */
    }
    this.enabled = stored !== 'off';
  }

  setEnabled(on: boolean): void {
    this.enabled = on;
    try {
      localStorage.setItem(SOUND_KEY, on ? 'on' : 'off');
    } catch {
      /* ignore */
    }
    if (on) this.unlock();
  }

  /** Call from a user gesture (tap/click). */
  unlock(): void {
    if (!this.enabled) return;
    try {
      if (!this.ctx) {
        const Ctor = window.AudioContext ?? (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
        if (!Ctor) return;
        this.ctx = new Ctor();
      }
      if (this.ctx.state === 'suspended') void this.ctx.resume();
      this.loading ??= this.loadAll();
    } catch {
      this.ctx = null;
    }
  }

  play(effect: SoundEffect): void {
    if (!this.enabled || !this.ctx) return;
    const buffer = this.buffers.get(effect);
    if (!buffer) return;
    try {
      const src = this.ctx.createBufferSource();
      src.buffer = buffer;
      const gain = this.ctx.createGain();
      gain.gain.value = 0.7;
      src.connect(gain).connect(this.ctx.destination);
      src.start();
    } catch {
      /* ignore audio errors */
    }
  }

  private async loadAll(): Promise<void> {
    const names: SoundEffect[] = ['dice', 'move', 'capture', 'enter', 'finish', 'win', 'lose', 'turn', 'tap'];
    await Promise.all(
      names.map(async (n) => {
        try {
          const res = await fetch(`${this.base}sfx/sfx_${n}.wav`);
          const data = await res.arrayBuffer();
          const buf = await this.ctx!.decodeAudioData(data);
          this.buffers.set(n, buf);
        } catch {
          /* a missing sound is not an error */
        }
      }),
    );
  }
}

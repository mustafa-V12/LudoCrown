// A 3D die built from CSS transforms. roll(value) tumbles it and lands on the given face.
import { h, reducedMotion, sleep } from './dom';

// Face layout (opposite faces sum to 7): front 1, top 2, right 3, left 4, bottom 5, back 6.
const PIPS: Record<number, number[]> = { 1: [4], 2: [0, 8], 3: [0, 4, 8], 4: [0, 2, 6, 8], 5: [0, 2, 4, 6, 8], 6: [0, 2, 3, 5, 6, 8] };
const FACE_ROT: Record<number, [number, number]> = { 1: [0, 0], 2: [-90, 0], 3: [0, -90], 4: [0, 90], 5: [90, 0], 6: [0, 180] };
const SIDES: [number, string][] = [[1, 'front'], [2, 'top'], [3, 'right'], [4, 'left'], [5, 'bottom'], [6, 'back']];

export class Die3D {
  readonly element: HTMLElement;
  private readonly cube: HTMLElement;
  private rx = 0;
  private ry = 0;
  value = 0;

  constructor(className = '') {
    this.cube = h('div', { class: 'cube' });
    for (const [n, side] of SIDES) {
      const face = h('div', { class: `side ${side}` });
      for (let i = 0; i < 9; i++) face.append(h('span', { class: PIPS[n].includes(i) ? 'pip on' : 'pip' }));
      this.cube.append(face);
    }
    this.element = h('div', { class: `die3d ${className}` }, this.cube);
    this.show(1, false);
    this.value = 0;
  }

  /** Shows a face. With `spin`, tumbles forward a few turns first. */
  show(value: number, spin: boolean): void {
    const [bx, by] = FACE_ROT[value];
    if (spin) {
      // Always move forward by whole turns plus the face offset, so every roll looks like a real tumble.
      const turnsX = 2 + Math.floor(Math.random() * 2), turnsY = 1 + Math.floor(Math.random() * 2);
      this.rx = (Math.ceil(this.rx / 360) + turnsX) * 360 + bx;
      this.ry = (Math.ceil(this.ry / 360) + turnsY) * 360 + by;
    } else {
      this.rx = Math.round(this.rx / 360) * 360 + bx;
      this.ry = Math.round(this.ry / 360) * 360 + by;
    }
    this.cube.style.transition = spin ? '' : 'none';
    this.cube.style.transform = `rotateX(${this.rx}deg) rotateY(${this.ry}deg)`;
    this.value = value;
  }

  async roll(value: number): Promise<void> {
    if (reducedMotion()) {
      this.show(value, false);
      return;
    }
    this.element.classList.remove('landed');
    this.element.classList.add('tumbling');
    void this.cube.offsetWidth; // restart transition
    this.show(value, true);
    await sleep(720);
    this.element.classList.remove('tumbling');
    this.element.classList.add('landed');
  }
}

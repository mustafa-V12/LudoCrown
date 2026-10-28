// Ludo Crown's "royal board-game night" palette, as in src/LudoCrown.Mobile/Controls/Palette.cs.
import { PlayerColor } from '../engine/model';

export const Palette = {
  night: '#140C2E',
  velvet: '#21154A',
  plum: '#2E1B5C',
  gold: '#E9B949',
  goldDeep: '#B8862B',
  ivory: '#FBF3E4',
  line: '#D2BF98',
  ink: '#1A1230',
} as const;

const BASE: Record<PlayerColor, string> = {
  [PlayerColor.Ruby]: '#D7263D',
  [PlayerColor.Emerald]: '#17A673',
  [PlayerColor.Topaz]: '#F2A71B',
  [PlayerColor.Sapphire]: '#2F6FE0',
};

export const colorName = (c: PlayerColor): string => PlayerColor[c];

interface Hsl { h: number; s: number; l: number }

function toHsl(hex: string): Hsl {
  const n = parseInt(hex.slice(1), 16);
  const r = ((n >> 16) & 255) / 255, g = ((n >> 8) & 255) / 255, b = (n & 255) / 255;
  const max = Math.max(r, g, b), min = Math.min(r, g, b);
  const l = (max + min) / 2;
  if (max === min) return { h: 0, s: 0, l };
  const d = max - min;
  const s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
  const h = max === r ? (g - b) / d + (g < b ? 6 : 0) : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
  return { h: h * 60, s, l };
}

const css = (c: Hsl, alpha = 1): string =>
  `hsla(${c.h.toFixed(1)}, ${(c.s * 100).toFixed(1)}%, ${(c.l * 100).toFixed(1)}%, ${alpha})`;

export const of = (c: PlayerColor, alpha = 1): string => css(toHsl(BASE[c]), alpha);

export function light(c: PlayerColor, alpha = 1): string {
  const h = toHsl(BASE[c]);
  return css({ ...h, l: Math.min(0.82, h.l + 0.3) }, alpha);
}

export function dark(c: PlayerColor, alpha = 1): string {
  const h = toHsl(BASE[c]);
  return css({ ...h, l: Math.max(0.18, h.l - 0.18) }, alpha);
}

/** The colour washed out, for yards of colours that are not playing. */
export function muted(c: PlayerColor, l = 0.55): string {
  const h = toHsl(BASE[c]);
  return css({ ...h, s: 0.15, l });
}

/** Home-column squares get gradually lighter towards the crown. */
export function homeStep(c: PlayerColor, step: number): string {
  const h = toHsl(BASE[c]);
  return css({ ...h, l: Math.min(0.75, h.l + 0.08 * step) });
}

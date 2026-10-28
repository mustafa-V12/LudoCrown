// Original Ludo Crown illustrations and icons as inline SVG (lobby tiles, mode cards, toolbar icons).
const RUBY = '#D7263D', EMERALD = '#17A673', TOPAZ = '#F2A71B', SAPPHIRE = '#2F6FE0', GOLD = '#E9B949', GOLD_DEEP = '#B8862B', IVORY = '#FBF3E4', INK = '#1A1230';

/** A small board seen at an angle (isometric), used on the lobby tiles. */
function isoBoard(x: number, y: number, size: number, colors: string[]): string {
  const s = size / 2;
  const q = (cx: number, cy: number, c: string) => `<rect x="${cx}" y="${cy}" width="${s * 0.86}" height="${s * 0.86}" rx="${s * 0.12}" fill="${c}"/>`;
  return `<g transform="translate(${x} ${y}) scale(1 0.58) rotate(45)">` +
    `<rect x="${-s - 6}" y="${-s - 6}" width="${size + 12}" height="${size + 12}" rx="10" fill="${GOLD_DEEP}"/>` +
    `<rect x="${-s - 3}" y="${-s - 3}" width="${size + 6}" height="${size + 6}" rx="8" fill="${GOLD}"/>` +
    `<rect x="${-s}" y="${-s}" width="${size}" height="${size}" rx="6" fill="${IVORY}"/>` +
    q(-s + s * 0.07, -s + s * 0.07, colors[1]) + q(s * 0.07, -s + s * 0.07, colors[2]) +
    q(-s + s * 0.07, s * 0.07, colors[0]) + q(s * 0.07, s * 0.07, colors[3]) +
    `<rect x="${-s * 0.18}" y="${-s}" width="${s * 0.36}" height="${size}" fill="${IVORY}"/><rect x="${-s}" y="${-s * 0.18}" width="${size}" height="${s * 0.36}" fill="${IVORY}"/>` +
    `<circle r="${s * 0.24}" fill="${INK}" stroke="${GOLD}" stroke-width="2"/></g>`;
}

/** A glossy coin-like piece with a crown emblem. */
export function pieceSvg(x: number, y: number, r: number, color: string): string {
  return `<g transform="translate(${x} ${y})">` +
    `<ellipse cy="${r * 0.55}" rx="${r}" ry="${r * 0.45}" fill="rgba(0,0,0,.28)"/>` +
    `<circle r="${r}" fill="${GOLD_DEEP}"/><circle r="${r * 0.9}" fill="${GOLD}"/><circle r="${r * 0.74}" fill="${color}"/>` +
    `<ellipse cx="${-r * 0.2}" cy="${-r * 0.32}" rx="${r * 0.42}" ry="${r * 0.2}" fill="rgba(255,255,255,.35)"/>` +
    `<path d="M${-r * 0.38} ${r * 0.22} L${-r * 0.44} ${-r * 0.2} L${-r * 0.16} ${0} L0 ${-r * 0.32} L${r * 0.16} 0 L${r * 0.44} ${-r * 0.2} L${r * 0.38} ${r * 0.22} Z" fill="${IVORY}"/></g>`;
}

function dieSvg(x: number, y: number, s: number, rot: number, pips: number[][]): string {
  return `<g transform="translate(${x} ${y}) rotate(${rot})">` +
    `<rect x="${-s / 2}" y="${-s / 2 + 3}" width="${s}" height="${s}" rx="${s * 0.22}" fill="#C9B994"/>` +
    `<rect x="${-s / 2}" y="${-s / 2}" width="${s}" height="${s}" rx="${s * 0.22}" fill="#FFFDF8"/>` +
    pips.map(([px, py]) => `<circle cx="${px * s * 0.28}" cy="${py * s * 0.28}" r="${s * 0.09}" fill="${INK}"/>`).join('') + `</g>`;
}

const number = (text: string, x: number, y: number, size: number) =>
  `<text x="${x}" y="${y}" text-anchor="middle" font-family="'Baloo 2', system-ui, sans-serif" font-weight="800" font-size="${size}" ` +
  `fill="${IVORY}" stroke="${INK}" stroke-width="${size * 0.09}" paint-order="stroke" style="filter:drop-shadow(0 3px 0 rgba(0,0,0,.35))">${text}</text>`;

export function tileArt(kind: '2' | '4' | '3' | 'local'): string {
  const board = (colors: string[]) => isoBoard(100, 92, 92, colors);
  switch (kind) {
    case '2':
      return `<svg viewBox="0 0 200 160" aria-hidden="true">${board([RUBY, '#B9AED6', TOPAZ, '#B9AED6'])}` +
        pieceSvg(62, 116, 15, RUBY) + pieceSvg(140, 112, 15, TOPAZ) + dieSvg(104, 126, 26, -12, [[-1, -1], [1, 1]]) + number('2', 100, 62, 66) + `</svg>`;
    case '4':
      return `<svg viewBox="0 0 200 160" aria-hidden="true">${board([RUBY, EMERALD, TOPAZ, SAPPHIRE])}` +
        pieceSvg(48, 112, 13, EMERALD) + pieceSvg(78, 124, 13, RUBY) + pieceSvg(124, 124, 13, SAPPHIRE) + pieceSvg(154, 110, 13, TOPAZ) +
        dieSvg(101, 112, 24, 10, [[-1, -1], [1, -1], [-1, 1], [1, 1]]) + number('4', 100, 62, 66) + `</svg>`;
    case '3':
      return `<svg viewBox="0 0 120 100" aria-hidden="true">${isoBoard(60, 58, 58, [RUBY, EMERALD, TOPAZ, '#B9AED6'])}` +
        pieceSvg(36, 74, 9, RUBY) + pieceSvg(60, 80, 9, EMERALD) + pieceSvg(84, 74, 9, TOPAZ) + number('3', 60, 42, 40) + `</svg>`;
    case 'local':
      return `<svg viewBox="0 0 120 100" aria-hidden="true">` +
        `<rect x="38" y="16" width="44" height="72" rx="9" fill="${INK}" stroke="${GOLD}" stroke-width="3"/><rect x="43" y="24" width="34" height="52" rx="4" fill="${IVORY}"/>` +
        `<rect x="45" y="26" width="14" height="14" rx="2" fill="${EMERALD}"/><rect x="61" y="26" width="14" height="14" rx="2" fill="${TOPAZ}"/>` +
        `<rect x="45" y="60" width="14" height="14" rx="2" fill="${RUBY}"/><rect x="61" y="60" width="14" height="14" rx="2" fill="${SAPPHIRE}"/>` +
        `<path d="M14 54 q8 -14 22 -10" stroke="${GOLD}" stroke-width="4" fill="none" stroke-linecap="round"/><path d="M30 38 l7 6 -9 4z" fill="${GOLD}"/>` +
        `<path d="M106 46 q-8 14 -22 10" stroke="${GOLD}" stroke-width="4" fill="none" stroke-linecap="round"/><path d="M90 62 l-7 -6 9 -4z" fill="${GOLD}"/>` +
        `</svg>`;
  }
}

export function modeArt(mode: 'Classic' | 'Quick'): string {
  if (mode === 'Classic') {
    return `<svg viewBox="0 0 160 110" aria-hidden="true">` + crownPath(80, 40, 64) +
      pieceSvg(32, 86, 13, RUBY) + pieceSvg(64, 92, 13, EMERALD) + pieceSvg(96, 92, 13, TOPAZ) + pieceSvg(128, 86, 13, SAPPHIRE) + `</svg>`;
  }
  return `<svg viewBox="0 0 160 110" aria-hidden="true">` + crownPath(80, 40, 64) +
    `<path d="M112 14 L96 46 L110 46 L100 76 L128 38 L113 38 L124 14 Z" fill="#FFE07A" stroke="${GOLD_DEEP}" stroke-width="3" stroke-linejoin="round"/>` +
    pieceSvg(80, 90, 15, RUBY) + `</svg>`;
}

function crownPath(cx: number, cy: number, w: number): string {
  const h = w * 0.7, x = cx - w / 2, y = cy - h / 2;
  const p = (fx: number, fy: number) => `${(x + w * fx).toFixed(1)} ${(y + h * fy).toFixed(1)}`;
  return `<path d="M${p(0.08, 1)} L${p(0, 0.18)} L${p(0.3, 0.5)} L${p(0.5, 0)} L${p(0.7, 0.5)} L${p(1, 0.18)} L${p(0.92, 1)} Z" fill="${GOLD}" stroke="${GOLD_DEEP}" stroke-width="3" stroke-linejoin="round"/>` +
    `<circle cx="${cx}" cy="${y + h * 0.66}" r="${w * 0.07}" fill="${RUBY}"/>` +
    `<circle cx="${x + w * 0.08}" cy="${y + h * 0.18}" r="${w * 0.05}" fill="${GOLD}" stroke="${GOLD_DEEP}" stroke-width="2"/>` +
    `<circle cx="${x + w * 0.5}" cy="${y}" r="${w * 0.05}" fill="${GOLD}" stroke="${GOLD_DEEP}" stroke-width="2"/>` +
    `<circle cx="${x + w}" cy="${y + h * 0.18}" r="${w * 0.05}" fill="${GOLD}" stroke="${GOLD_DEEP}" stroke-width="2"/>`;
}

export function logoSvg(): string {
  return `<svg viewBox="0 0 120 90" aria-hidden="true">${crownPath(60, 40, 92)}</svg>`;
}

const icon = (body: string) => `<svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round">${body}</svg>`;

export const Icons = {
  menu: icon('<path d="M4 7h16M4 12h16M4 17h16"/>'),
  info: icon('<circle cx="12" cy="12" r="9"/><path d="M12 11v6M12 7.5v.01"/>'),
  back: icon('<path d="M15 5l-7 7 7 7"/>'),
  soundOn: icon('<path d="M4 9h4l5-4v14l-5-4H4z"/><path d="M16.5 8.5a5 5 0 0 1 0 7M19 6a8.5 8.5 0 0 1 0 12"/>'),
  soundOff: icon('<path d="M4 9h4l5-4v14l-5-4H4z"/><path d="M17 9l5 6M22 9l-5 6"/>'),
  settings: icon('<circle cx="12" cy="12" r="3.2"/><path d="M12 2.5v3M12 18.5v3M2.5 12h3M18.5 12h3M5.3 5.3l2.1 2.1M16.6 16.6l2.1 2.1M5.3 18.7l2.1-2.1M16.6 7.4l2.1-2.1"/>'),
  home: icon('<path d="M3 11l9-7 9 7"/><path d="M5 10v10h14V10"/>'),
  book: icon('<path d="M4 5a2 2 0 0 1 2-2h13v16H6a2 2 0 0 0-2 2z"/><path d="M4 19V5"/>'),
  close: icon('<path d="M6 6l12 12M18 6L6 18"/>'),
  trophy: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 3h10v4a5 5 0 0 1-10 0z" fill="${GOLD}" stroke="${GOLD_DEEP}" stroke-width="1.5"/><path d="M7 5H4a3 3 0 0 0 3 4M17 5h3a3 3 0 0 1-3 4" stroke="${GOLD_DEEP}" stroke-width="1.5" fill="none"/><path d="M10 12h4v4h-4z" fill="${GOLD_DEEP}"/><rect x="7" y="16" width="10" height="4" rx="1.5" fill="${GOLD}" stroke="${GOLD_DEEP}" stroke-width="1.5"/></svg>`,
  crown: `<svg viewBox="0 0 24 20" aria-hidden="true"><path d="M3 18 L1.5 4 L7 9 L12 1.5 L17 9 L22.5 4 L21 18 Z" fill="${GOLD}" stroke="${GOLD_DEEP}" stroke-width="1.5" stroke-linejoin="round"/><circle cx="12" cy="13" r="1.8" fill="${RUBY}"/></svg>`,
  dice: `<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="3" width="18" height="18" rx="5" fill="${IVORY}" stroke="${GOLD_DEEP}" stroke-width="1.5"/><circle cx="8.5" cy="8.5" r="1.7" fill="${INK}"/><circle cx="15.5" cy="15.5" r="1.7" fill="${INK}"/><circle cx="12" cy="12" r="1.7" fill="${INK}"/></svg>`,
};

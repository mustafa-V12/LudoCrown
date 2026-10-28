// The lobby: profile bar, logo, big "2 Players" / "4 Players" tiles, smaller tiles and a bottom bar.
import { GameModes } from '../engine/model';
import type { SavedGame } from '../game/controller';
import { Icons, logoSvg, tileArt } from './art';
import { avatarSvg } from './avatars';
import { h, svg } from './dom';
import type { Prefs, Stats } from './prefs';

export interface LobbyActions {
  onComputer(players: number): void;
  onLocal(): void;
  onResume(): void;
  onDiscard(): void;
  onRules(): void;
  onSettings(): void;
}

export function renderLobby(prefs: Prefs, stats: Stats, save: SavedGame | null, a: LobbyActions): HTMLElement {
  const isIos = /iPhone|iPad|iPod/.test(navigator.userAgent);
  const standalone = (navigator as unknown as { standalone?: boolean }).standalone === true || matchMedia('(display-mode: standalone)').matches;

  const top = h('header', { class: 'lobby-top' },
    h('button', { class: 'profile-chip', type: 'button', onclick: a.onSettings, 'aria-label': 'Profile and settings' },
      svg(avatarSvg(prefs.avatar), 'avatar sm'),
      h('span', { class: 'pc-text' }, h('span', { class: 'pc-name' }, prefs.name), h('span', { class: 'pc-sub' }, 'Edit profile'))),
    h('div', { class: 'stat-pill', title: 'Wins against the computer on this device' }, svg(Icons.trophy, 'pill-icon'), h('span', {}, String(stats.wins))),
    h('div', { class: 'stat-pill', title: 'Games played on this device' }, svg(Icons.dice, 'pill-icon'), h('span', {}, String(stats.played))),
    h('button', { class: 'round-btn', type: 'button', 'aria-label': 'Settings', onclick: a.onSettings }, svg(Icons.settings)));

  const logo = h('div', { class: 'logo' }, svg(logoSvg(), 'logo-crown'), h('h1', { class: 'logo-text' }, h('span', {}, 'LUDO'), h('span', {}, 'CROWN')));

  const resume = save
    ? h('div', { class: 'resume-banner' },
        h('div', { class: 'rb-avatars' }, ...save.seats.slice(0, 4).map((s) => svg(avatarSvg(s.avatar ?? 'king'), 'avatar xs'))),
        h('div', { class: 'rb-text' }, h('strong', {}, 'Continue game'), h('span', {}, `${GameModes[save.state.rules.mode].name} · ${save.seats.length} players`)),
        h('button', { class: 'btn3d gold', type: 'button', onclick: a.onResume }, 'GO'),
        h('button', { class: 'rb-x', type: 'button', 'aria-label': 'Discard saved game', onclick: a.onDiscard }, svg(Icons.close)))
    : null;

  const bigTile = (players: 2 | 4, cls: string) =>
    h('button', { class: `tile big ${cls}`, type: 'button', onclick: () => a.onComputer(players), 'aria-label': `${players} players against the computer` },
      svg(tileArt(players === 2 ? '2' : '4'), 'tile-art'),
      h('span', { class: 'tile-label' }, `${players} Players`),
      h('span', { class: 'tile-sub' }, 'vs Computer'));

  const smallTile = (label: string, art: string, cls: string, fn: () => void, sub: string) =>
    h('button', { class: `tile small ${cls}`, type: 'button', onclick: fn }, svg(art, 'tile-art'), h('span', { class: 'tile-label' }, label), h('span', { class: 'tile-sub' }, sub));

  const rulesArt = `<svg viewBox="0 0 120 100" aria-hidden="true"><rect x="34" y="14" width="52" height="68" rx="6" fill="#FBF3E4" stroke="#B8862B" stroke-width="3"/><path d="M44 32h32M44 44h32M44 56h22" stroke="#B9AED6" stroke-width="5" stroke-linecap="round"/><circle cx="82" cy="74" r="16" fill="#E9B949" stroke="#B8862B" stroke-width="3"/><text x="82" y="81" text-anchor="middle" font-family="'Baloo 2',sans-serif" font-weight="800" font-size="22" fill="#1A1230">?</text></svg>`;

  return h('main', { class: 'screen lobby' },
    top,
    logo,
    resume,
    h('div', { class: 'tiles-big' }, bigTile(2, 'gold-tile'), bigTile(4, 'violet-tile')),
    h('div', { class: 'tiles-small' },
      smallTile('3 Players', tileArt('3'), 'ruby-tile', () => a.onComputer(3), 'vs Computer'),
      smallTile('Pass & Play', tileArt('local'), 'emerald-tile', a.onLocal, 'Friends, 1 phone'),
      smallTile('How to play', rulesArt, 'sapphire-tile', a.onRules, 'Rules')),
    isIos && !standalone
      ? h('p', { class: 'tip' }, svg(Icons.crown, 'tip-icon'), h('span', {}, 'Tap ', h('strong', {}, 'Share'), ' → ', h('strong', {}, 'Add to Home Screen'), ' to play full-screen like an app.'))
      : null,
    h('p', { class: 'fine center' }, 'Online matches, gem rerolls, Shields, the store and leagues are in the Ludo Crown app.'),
    h('nav', { class: 'bottom-bar', 'aria-label': 'Main' },
      h('button', { class: 'bb-item', type: 'button', onclick: a.onRules }, svg(Icons.book), h('span', {}, 'Rules')),
      h('button', { class: 'bb-item home', type: 'button', 'aria-current': 'page' }, svg(Icons.home), h('span', {}, 'Lobby')),
      h('button', { class: 'bb-item', type: 'button', onclick: a.onSettings }, svg(Icons.settings), h('span', {}, 'Settings'))));
}

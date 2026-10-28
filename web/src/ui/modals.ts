// Pop-up panels: mode picker, rules, profile & settings, in-game menu.
import type { BotDifficulty } from '../engine/bots';
import { type GameMode, AllModes, GameModes } from '../engine/model';
import { Icons, modeArt } from './art';
import { AVATARS, avatarSvg } from './avatars';
import { h, svg } from './dom';
import type { Prefs } from './prefs';

export interface Sheet {
  element: HTMLElement;
  close(): void;
}

/** A centred game-style panel with a ribbon title and a close button. */
export function openPanel(title: string, body: Node[], opts: { onClose?: () => void; className?: string } = {}): Sheet {
  let closed = false;
  const close = () => {
    if (closed) return;
    closed = true;
    overlay.classList.add('closing');
    setTimeout(() => overlay.remove(), 180);
    opts.onClose?.();
  };
  const closeBtn = h('button', { class: 'panel-close', type: 'button', 'aria-label': 'Close', onclick: close }, svg(Icons.close));
  const panel = h('div', { class: `panel ${opts.className ?? ''}`, role: 'dialog', 'aria-modal': 'true', 'aria-label': title },
    h('div', { class: 'ribbon' }, h('span', {}, title)), closeBtn, h('div', { class: 'panel-body' }, ...body));
  const overlay = h('div', { class: 'overlay' }, panel);
  overlay.addEventListener('click', (e) => { if (e.target === overlay) close(); });
  const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') { close(); document.removeEventListener('keydown', onKey); } };
  document.addEventListener('keydown', onKey);
  document.body.append(overlay);
  closeBtn.focus({ preventScroll: true });
  return { element: overlay, close };
}

function chips<T extends string | number>(options: { value: T; label: string; sub?: string }[], current: T, onPick: (v: T) => void, cls = ''): HTMLElement {
  const row = h('div', { class: `chips ${cls}`, role: 'radiogroup' });
  const render = (cur: T) => {
    row.replaceChildren(...options.map((o) => h('button', {
      class: 'chip3d' + (o.value === cur ? ' on' : ''), type: 'button', role: 'radio', 'aria-checked': String(o.value === cur),
      onclick: () => { onPick(o.value); render(o.value); },
    }, h('span', { class: 'chip-label' }, o.label), o.sub ? h('span', { class: 'chip-sub' }, o.sub) : null)));
  };
  render(current);
  return row;
}

export interface ModeChoice {
  mode: GameMode;
  difficulty: BotDifficulty;
  players: number;
}

/** Choose Classic or Quick (and difficulty or player count), then Play. */
export function openModePicker(
  title: string,
  initial: ModeChoice,
  kind: 'computer' | 'local',
  onPlay: (choice: ModeChoice) => void,
): Sheet {
  const choice = { ...initial };
  const cards = h('div', { class: 'mode-cards', role: 'radiogroup', 'aria-label': 'Game mode' });
  const renderCards = () => {
    cards.replaceChildren(...AllModes.map((m) => h('button', {
      class: 'mode-card' + (m.mode === choice.mode ? ' on' : ''), type: 'button', role: 'radio', 'aria-checked': String(m.mode === choice.mode),
      onclick: () => { choice.mode = m.mode; renderCards(); rule.textContent = GameModes[choice.mode].winCondition; },
    },
      svg(modeArt(m.mode), 'mode-art'),
      h('span', { class: 'mode-name' }, m.name),
      h('span', { class: 'mode-tag' }, m.tagline),
      m.mode === choice.mode ? h('span', { class: 'check' }, '✓') : null)));
  };
  renderCards();
  const rule = h('p', { class: 'mode-rule' }, GameModes[choice.mode].winCondition);

  const extra = kind === 'computer'
    ? [h('h3', { class: 'panel-h' }, 'Difficulty'),
       chips<BotDifficulty>([
         { value: 'Easy', label: 'Easy', sub: 'Relaxed' },
         { value: 'Normal', label: 'Normal', sub: 'Smart' },
         { value: 'Hard', label: 'Hard', sub: 'Ruthless' },
       ], choice.difficulty, (v) => { choice.difficulty = v; })]
    : [h('h3', { class: 'panel-h' }, 'Players on this phone'),
       chips([2, 3, 4].map((n) => ({ value: n, label: `${n}` })), choice.players, (v) => { choice.players = v; }, 'numbers')];

  let sheet: Sheet;
  const play = h('button', { class: 'btn3d gold big', type: 'button', onclick: () => { sheet.close(); onPlay(choice); } }, 'PLAY');
  sheet = openPanel(title, [h('h3', { class: 'panel-h' }, 'Game mode'), cards, rule, ...extra, play], { className: 'mode-panel' });
  return sheet;
}

export function openRules(): Sheet {
  const section = (title: string, text: Node | string) => h('div', { class: 'rule-item' }, h('h3', {}, title), h('p', {}, text));
  return openPanel('How to play', [
    section('Your turn', 'Tap the die next to your avatar, then tap one of your glowing pieces to move it that many squares. If no piece can move, the turn passes — even after a 6.'),
    section('Leaving the yard', 'A piece enters the board on a 6, onto your coloured start square (the one with the arrow).'),
    section('Moving', 'Pieces travel clockwise around the board, then up their own coloured column to the crown. You need the exact number to reach the crown.'),
    section('Capturing', 'Land exactly on an opponent to send their piece back home. Stars and start squares are safe. Your own pieces may share a square.'),
    section('Extra rolls', 'Rolling a 6, capturing, or crowning a piece gives you another roll. Three 6s in a row forfeit the turn.'),
    section('Winning', h('span', {}, h('strong', {}, 'Classic: '), GameModes.Classic.winCondition, h('br'), h('strong', {}, 'Quick: '), GameModes.Quick.winCondition)),
    h('p', { class: 'fine' }, 'Your game is saved on this device after every move, so you can close the page and continue later.'),
  ], { className: 'rules-panel' });
}

/** Profile (name and avatar) plus device settings. */
export function openSettings(prefs: Prefs, sound: { enabled: boolean; setEnabled(on: boolean): void }, onChange: (p: Prefs) => void, onClose?: () => void): Sheet {
  const p = { ...prefs };
  const preview = svg(avatarSvg(p.avatar), 'avatar big');
  const grid = h('div', { class: 'avatar-grid', role: 'radiogroup', 'aria-label': 'Avatar' });
  const renderGrid = () => grid.replaceChildren(...AVATARS.map((a) => h('button', {
    class: 'avatar-pick' + (a.id === p.avatar ? ' on' : ''), type: 'button', role: 'radio', 'aria-checked': String(a.id === p.avatar), 'aria-label': a.name,
    onclick: () => { p.avatar = a.id; preview.innerHTML = avatarSvg(a.id); renderGrid(); onChange({ ...p }); },
  }, svg(avatarSvg(a.id), 'avatar'))));
  renderGrid();

  const name = h('input', { type: 'text', maxlength: 14, value: p.name, autocomplete: 'nickname', enterkeyhint: 'done', 'aria-label': 'Your name' });
  name.addEventListener('input', () => { p.name = name.value.slice(0, 14); onChange({ ...p }); });

  const toggle = (label: string, on: boolean, change: (v: boolean) => void) => {
    const input = h('input', { type: 'checkbox', checked: on, role: 'switch' });
    input.addEventListener('change', () => change(input.checked));
    return h('label', { class: 'switch-row' }, h('span', {}, label), input, h('span', { class: 'switch' }));
  };

  return openPanel('Profile & settings', [
    h('div', { class: 'profile-head' }, preview, h('label', { class: 'name-field' }, h('span', {}, 'Name'), name)),
    h('h3', { class: 'panel-h' }, 'Choose your avatar'),
    grid,
    h('h3', { class: 'panel-h' }, 'Settings'),
    toggle('Sound effects', sound.enabled, (v) => sound.setEnabled(v)),
    toggle('Move automatically when only one move is possible', p.autoMove, (v) => { p.autoMove = v; onChange({ ...p }); }),
  ], { className: 'settings-panel', onClose });
}

/** In-game menu. */
export function openGameMenu(actions: { onResume(): void; onRules(): void; onHome(): void }): Sheet {
  let sheet: Sheet;
  const item = (label: string, icon: string, fn: () => void, cls = '') =>
    h('button', { class: `menu-item ${cls}`, type: 'button', onclick: () => { sheet.close(); fn(); } }, svg(icon, 'mi-icon'), h('span', {}, label));
  sheet = openPanel('Menu', [
    item('Resume', Icons.dice, actions.onResume),
    item('How to play', Icons.book, actions.onRules),
    item('Leave to lobby', Icons.home, actions.onHome, 'danger'),
    h('p', { class: 'fine' }, 'Your game is saved — you can continue it from the lobby.'),
  ], { className: 'menu-panel' });
  return sheet;
}

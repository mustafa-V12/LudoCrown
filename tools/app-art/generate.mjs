// Generates the MAUI app's SVG artwork (avatars, lobby tiles, mode cards, icons) from the web version's sources,
// so the app and the web version share one look. Run: node --experimental-strip-types tools/app-art/generate.mjs
import { writeFileSync } from 'node:fs';
import { avatarSvg } from '../../web/src/ui/avatars.ts';
import { modeArt, tileArt, pieceSvg } from '../../web/src/ui/art.ts';

const out = 'src/LudoCrown.Mobile/Resources/Images/';
const sized = (svg, w, h) => (svg.includes('xmlns=') ? svg : svg.replace('<svg ', '<svg xmlns="http://www.w3.org/2000/svg" ')).replace('<svg ', `<svg width="${w}" height="${h}" `).replace(/ aria-hidden="true"| role="img" aria-label="[^"]*"/g, '');
const noText = (svg) => svg.replace(/<text[\s\S]*?<\/text>/g, '');
const write = (name, svg) => { writeFileSync(out + name, svg + '\n'); console.log(name); };

// Avatars: the app keeps its existing ids (server-validated); each id gets one of the new characters.
const map = { crown: 'king', swan: 'queen', knight: 'knight', jester: 'jester', star: 'wizard', rook: 'pirate', gem: 'princess', lion: 'lion' };
for (const [id, art] of Object.entries(map)) write(`avatar_${id}.svg`, sized(avatarSvg(art), 128, 128));

// Lobby tiles (numbers are drawn by the app as text so they use the app font).
write('tile_2p.svg', sized(noText(tileArt('2')), 200, 160));
write('tile_4p.svg', sized(noText(tileArt('4')), 200, 160));
write('tile_local.svg', sized(tileArt('local'), 120, 100));
write('mode_classic.svg', sized(modeArt('Classic'), 160, 110));
write('mode_quick.svg', sized(modeArt('Quick'), 160, 110));

const GOLD = '#E9B949', GOLD_DEEP = '#B8862B', IVORY = '#FBF3E4', INK = '#1A1230', RUBY = '#D7263D', EMERALD = '#17A673', SAPPHIRE = '#2F6FE0', TOPAZ = '#F2A71B';
const svg = (w, h, vb, body) => `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" viewBox="${vb}">${body}</svg>`;

write('tile_league.svg', svg(120, 100, '0 0 120 100',
  `<path d="M38 14h44v22a22 22 0 0 1-44 0z" fill="${GOLD}" stroke="${GOLD_DEEP}" stroke-width="3"/>` +
  `<path d="M38 20H24a12 12 0 0 0 14 18M82 20h14a12 12 0 0 1-14 18" stroke="${GOLD_DEEP}" stroke-width="4" fill="none"/>` +
  `<rect x="54" y="56" width="12" height="14" fill="${GOLD_DEEP}"/><rect x="40" y="70" width="40" height="12" rx="4" fill="${GOLD}" stroke="${GOLD_DEEP}" stroke-width="3"/>` +
  `<path d="M60 22 l4 8 9 1.3-6.5 6.3 1.5 9-8-4.2-8 4.2 1.5-9-6.5-6.3 9-1.3z" fill="${IVORY}"/>`));
write('tile_store.svg', svg(120, 100, '0 0 120 100',
  `<ellipse cx="44" cy="76" rx="22" ry="8" fill="${GOLD_DEEP}"/><ellipse cx="44" cy="70" rx="22" ry="8" fill="${GOLD}"/><ellipse cx="44" cy="62" rx="22" ry="8" fill="${GOLD_DEEP}"/><ellipse cx="44" cy="56" rx="22" ry="8" fill="${GOLD}" stroke="${GOLD_DEEP}" stroke-width="2"/>` +
  `<path d="M70 30 L84 16 L100 30 L85 62 Z" fill="#2FD39B" stroke="#0B6B47" stroke-width="3" stroke-linejoin="round"/><path d="M70 30 L100 30 M84 16 L80 30 L85 62 M84 16 L90 30 L85 62" stroke="#0B6B47" stroke-width="2" fill="none"/>`));
write('tile_private.svg', svg(120, 100, '0 0 120 100',
  `<rect x="22" y="44" width="76" height="40" rx="10" fill="#7B4A2A" stroke="${GOLD_DEEP}" stroke-width="3"/><rect x="28" y="50" width="64" height="28" rx="6" fill="#2FA36B"/>` +
  pieceSvg(46, 60, 9, RUBY) + pieceSvg(74, 60, 9, SAPPHIRE) +
  `<circle cx="60" cy="24" r="14" fill="${GOLD}" stroke="${GOLD_DEEP}" stroke-width="3"/><rect x="56" y="30" width="8" height="18" rx="2" fill="${GOLD}" stroke="${GOLD_DEEP}" stroke-width="2"/><circle cx="60" cy="22" r="5" fill="${INK}"/>`));
write('tile_offline.svg', svg(120, 100, '0 0 120 100',
  `<rect x="30" y="26" width="60" height="46" rx="14" fill="#C9D1DE" stroke="#7D8899" stroke-width="3"/><rect x="38" y="34" width="44" height="26" rx="8" fill="${INK}"/>` +
  `<circle cx="50" cy="47" r="5" fill="#5BE3B0"/><circle cx="70" cy="47" r="5" fill="#5BE3B0"/><path d="M60 26 V14" stroke="#7D8899" stroke-width="3"/><circle cx="60" cy="12" r="5" fill="${RUBY}"/>` +
  `<rect x="44" y="72" width="32" height="12" rx="4" fill="#7D8899"/>`));

// Icons (explicit colours: the app draws them as images).
const icon = (body, color = IVORY) => svg(48, 48, '0 0 24 24', `<g fill="none" stroke="${color}" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round">${body}</g>`);
write('icon_menu.svg', icon('<path d="M4 7h16M4 12h16M4 17h16"/>'));
write('icon_info.svg', icon('<circle cx="12" cy="12" r="9"/><path d="M12 11v6M12 7.5v.01"/>'));
write('icon_settings.svg', icon('<circle cx="12" cy="12" r="3.2"/><path d="M12 2.5v3M12 18.5v3M2.5 12h3M18.5 12h3M5.3 5.3l2.1 2.1M16.6 16.6l2.1 2.1M5.3 18.7l2.1-2.1M16.6 7.4l2.1-2.1"/>'));
write('icon_home.svg', icon('<path d="M3 11l9-7 9 7"/><path d="M5 10v10h14V10"/>', INK));
write('icon_friends.svg', icon('<circle cx="9" cy="8" r="3.5"/><path d="M2.5 20a6.5 6.5 0 0 1 13 0"/><circle cx="17" cy="9" r="2.6"/><path d="M16 14.5a5 5 0 0 1 6 5.5"/>', '#C3B8E0'));
write('icon_store.svg', icon('<path d="M4 9h16l-1.5 11h-13z"/><path d="M8 9V7a4 4 0 0 1 8 0v2"/>', '#C3B8E0'));
write('icon_league.svg', icon('<path d="M7 4h10v5a5 5 0 0 1-10 0z"/><path d="M7 6H4a3 3 0 0 0 3 4M17 6h3a3 3 0 0 1-3 4M12 14v4M8 20h8"/>', '#C3B8E0'));
write('icon_profile.svg', icon('<circle cx="12" cy="8" r="4"/><path d="M4 21a8 8 0 0 1 16 0"/>', '#C3B8E0'));
write('icon_book.svg', icon('<path d="M4 5a2 2 0 0 1 2-2h13v16H6a2 2 0 0 0-2 2z"/><path d="M4 19V5"/>', '#C3B8E0'));
write('icon_close.svg', icon('<path d="M6 6l12 12M18 6L6 18"/>', '#FFFFFF'));
write('icon_reroll.svg', icon('<path d="M20 12a8 8 0 1 1-2.3-5.7"/><path d="M20 4v5h-5"/>', '#FFFFFF'));
write('icon_chat.svg', icon('<path d="M4 5h16v11H9l-5 4z"/>', '#FFFFFF'));
write('icon_gem.svg', svg(48, 48, '0 0 24 24', `<path d="M5 9 L9 4 H15 L19 9 L12 21 Z" fill="#2FD39B" stroke="#0B6B47" stroke-width="1.4" stroke-linejoin="round"/><path d="M5 9 H19 M9 4 L10.5 9 L12 21 M15 4 L13.5 9 L12 21" stroke="#0B6B47" stroke-width="1" fill="none"/><path d="M9.5 5 L8 8.5" stroke="#D9FFF0" stroke-width="1.4" stroke-linecap="round"/>`));
write('icon_coin.svg', svg(48, 48, '0 0 24 24', `<circle cx="12" cy="12" r="9.5" fill="${GOLD}" stroke="${GOLD_DEEP}" stroke-width="1.6"/><circle cx="12" cy="12" r="6.5" fill="none" stroke="${GOLD_DEEP}" stroke-width="1.2"/><path d="M8.6 14.2 L8 9.4 L10.2 11.2 L12 8.4 L13.8 11.2 L16 9.4 L15.4 14.2 Z" fill="#FFF3C4"/>`));
write('icon_crown.svg', svg(48, 40, '0 0 24 20', `<path d="M3 18 L1.5 4 L7 9 L12 1.5 L17 9 L22.5 4 L21 18 Z" fill="${GOLD}" stroke="${GOLD_DEEP}" stroke-width="1.5" stroke-linejoin="round"/><circle cx="12" cy="13" r="1.8" fill="${RUBY}"/>`));
write('icon_trophy.svg', svg(48, 48, '0 0 24 24', `<path d="M7 3h10v4a5 5 0 0 1-10 0z" fill="${GOLD}" stroke="${GOLD_DEEP}" stroke-width="1.5"/><path d="M7 5H4a3 3 0 0 0 3 4M17 5h3a3 3 0 0 1-3 4" stroke="${GOLD_DEEP}" stroke-width="1.5" fill="none"/><path d="M10 12h4v4h-4z" fill="${GOLD_DEEP}"/><rect x="7" y="16" width="10" height="4" rx="1.5" fill="${GOLD}" stroke="${GOLD_DEEP}" stroke-width="1.5"/>`));
write('icon_shield.svg', svg(48, 48, '0 0 24 24', `<path d="M12 2.5 L20 5.5 V11 C20 16 16.5 19.5 12 21.5 C7.5 19.5 4 16 4 11 V5.5 Z" fill="${SAPPHIRE}" stroke="#FFFFFF" stroke-width="1.6"/><path d="M12 6 V18 M7.5 10.5 H16.5" stroke="#BFD6FF" stroke-width="1.6"/>`));
write('icon_sound.svg', icon('<path d="M4 9h4l5-4v14l-5-4H4z"/><path d="M16.5 8.5a5 5 0 0 1 0 7M19 6a8.5 8.5 0 0 1 0 12"/>'));
void TOPAZ; void EMERALD;

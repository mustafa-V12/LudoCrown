// Original Ludo Crown avatars, drawn as SVG (royal-court characters). No external art.
export interface AvatarInfo {
  id: string;
  name: string;
}

export const AVATARS: readonly AvatarInfo[] = [
  { id: 'king', name: 'King' },
  { id: 'queen', name: 'Queen' },
  { id: 'knight', name: 'Knight' },
  { id: 'jester', name: 'Jester' },
  { id: 'wizard', name: 'Wizard' },
  { id: 'pirate', name: 'Pirate' },
  { id: 'princess', name: 'Princess' },
  { id: 'lion', name: 'Lion' },
];

/** Avatars the computer players use, in seat order. */
export const BOT_AVATARS = ['queen', 'knight', 'wizard'];

const bg = (id: string, a: string, b: string) =>
  `<defs><radialGradient id="bg-${id}" cx="50%" cy="35%" r="75%"><stop offset="0" stop-color="${a}"/><stop offset="1" stop-color="${b}"/></radialGradient>` +
  `<clipPath id="clip-${id}"><circle cx="50" cy="50" r="50"/></clipPath></defs>` +
  `<circle cx="50" cy="50" r="50" fill="url(#bg-${id})"/>`;

const face = (skin: string, cheek = '#F28B82') =>
  `<circle cx="50" cy="57" r="24" fill="${skin}"/>` +
  `<ellipse cx="41" cy="56" rx="3.2" ry="3.8" fill="#2B1B3D"/><ellipse cx="59" cy="56" rx="3.2" ry="3.8" fill="#2B1B3D"/>` +
  `<circle cx="42.2" cy="54.6" r="1.1" fill="#fff"/><circle cx="60.2" cy="54.6" r="1.1" fill="#fff"/>` +
  `<circle cx="35" cy="64" r="4" fill="${cheek}" opacity="0.55"/><circle cx="65" cy="64" r="4" fill="${cheek}" opacity="0.55"/>`;

const smile = (color = '#7A2E2E') => `<path d="M43 66 Q50 72 57 66" stroke="${color}" stroke-width="2.6" fill="none" stroke-linecap="round"/>`;
const body = (color: string, trim = '#E9B949') =>
  `<ellipse cx="50" cy="104" rx="36" ry="24" fill="${color}"/><path d="M38 82 L50 92 L62 82" stroke="${trim}" stroke-width="3" fill="none"/>`;
const crown = (y: number, scale = 1, gem = '#D7263D') =>
  `<g transform="translate(50 ${y}) scale(${scale}) translate(-50 0)">` +
  `<path d="M30 14 L28 -6 L39 4 L50 -10 L61 4 L72 -6 L70 14 Z" fill="#E9B949" stroke="#B8862B" stroke-width="2" stroke-linejoin="round"/>` +
  `<circle cx="50" cy="6" r="3.2" fill="${gem}"/><circle cx="36" cy="9" r="2" fill="#17A673"/><circle cx="64" cy="9" r="2" fill="#2F6FE0"/></g>`;

const ART: Record<string, string> = {
  king:
    bg('king', '#F0566A', '#8E1326') + `<g clip-path="url(#clip-king)">` + body('#5B2A86') +
    face('#F2C29B') +
    `<path d="M28 60 Q30 84 50 88 Q70 84 72 60 Q66 74 50 75 Q34 74 28 60 Z" fill="#8A5A2B"/>` +
    `<path d="M42 70 Q50 66 58 70" stroke="#6B4320" stroke-width="3" fill="none" stroke-linecap="round"/>` + smile('#5A2A1A') +
    `<path d="M27 50 Q29 33 50 32 Q71 33 73 50 Q66 42 50 42 Q34 42 27 50 Z" fill="#8A5A2B"/>` +
    crown(27, 1.05) + `</g>`,
  queen:
    bg('queen', '#3FD39B', '#0B6B47') + `<g clip-path="url(#clip-queen)">` +
    `<path d="M22 60 Q20 30 50 28 Q80 30 78 60 L80 96 L20 96 Z" fill="#B5492B"/>` + body('#D7263D') +
    face('#F7D3B5') + smile() +
    `<path d="M27 54 Q30 34 50 33 Q70 34 73 54 Q64 40 50 41 Q36 40 27 54 Z" fill="#B5492B"/>` +
    `<path d="M36 34 L40 22 L45 30 L50 18 L55 30 L60 22 L64 34 Z" fill="#E9B949" stroke="#B8862B" stroke-width="1.6" stroke-linejoin="round"/>` +
    `<circle cx="50" cy="27" r="2.4" fill="#2F6FE0"/></g>`,
  knight:
    bg('knight', '#5A92F2', '#173E8C') + `<g clip-path="url(#clip-knight)">` + body('#8E9AAF', '#D7263D') +
    `<path d="M24 64 Q22 30 50 26 Q78 30 76 64 L76 80 L24 80 Z" fill="#C9D1DE" stroke="#7D8899" stroke-width="2"/>` +
    `<rect x="31" y="50" width="38" height="9" rx="4" fill="#2B1B3D"/>` +
    `<path d="M50 30 L50 80" stroke="#7D8899" stroke-width="2"/>` +
    `<circle cx="34" cy="70" r="1.8" fill="#7D8899"/><circle cx="66" cy="70" r="1.8" fill="#7D8899"/>` +
    `<path d="M50 26 Q62 6 78 10 Q66 14 60 26 Z" fill="#D7263D"/></g>`,
  jester:
    bg('jester', '#FFC94D', '#C47A06') + `<g clip-path="url(#clip-jester)">` + body('#7B3FB5', '#17A673') +
    face('#F2C29B') + `<path d="M41 65 Q50 75 59 65" stroke="#7A2E2E" stroke-width="2.6" fill="#fff" stroke-linecap="round"/>` +
    `<path d="M26 46 Q30 30 50 32 L50 46 Z" fill="#7B3FB5"/><path d="M74 46 Q70 30 50 32 L50 46 Z" fill="#17A673"/>` +
    `<path d="M50 34 Q34 26 20 14 Q24 32 30 44 Z" fill="#7B3FB5"/><path d="M50 34 Q66 26 80 14 Q76 32 70 44 Z" fill="#17A673"/>` +
    `<circle cx="20" cy="14" r="4.5" fill="#E9B949"/><circle cx="80" cy="14" r="4.5" fill="#E9B949"/>` +
    `<rect x="25" y="42" width="50" height="6" rx="3" fill="#E9B949"/></g>`,
  wizard:
    bg('wizard', '#8C6CF0', '#33197A') + `<g clip-path="url(#clip-wizard)">` + body('#2F4FB0', '#E9B949') +
    face('#F7D3B5') +
    `<path d="M28 62 Q28 92 50 96 Q72 92 72 62 Q64 74 50 74 Q36 74 28 62 Z" fill="#F4F1EA"/>` +
    `<path d="M40 70 Q50 64 60 70" stroke="#DCD6CA" stroke-width="3.4" fill="none" stroke-linecap="round"/>` +
    `<path d="M22 44 L50 2 L78 44 Z" fill="#2F4FB0"/><rect x="20" y="40" width="60" height="8" rx="4" fill="#24398A"/>` +
    `<path d="M45 22 l2 4 4 .6-3 2.8.8 4.2-3.8-2-3.8 2 .8-4.2-3-2.8 4-.6z" fill="#E9B949"/>` +
    `<circle cx="60" cy="32" r="2" fill="#FBF3E4"/><circle cx="38" cy="36" r="1.5" fill="#FBF3E4"/></g>`,
  pirate:
    bg('pirate', '#3CC7C0', '#0E6461') + `<g clip-path="url(#clip-pirate)">` + body('#1A1230', '#FBF3E4') +
    face('#D9A27A') + smile('#5A2A1A') +
    `<path d="M27 48 Q30 30 50 30 Q70 30 73 48 Z" fill="#D7263D"/><circle cx="34" cy="38" r="2" fill="#FBF3E4"/><circle cx="48" cy="35" r="2" fill="#FBF3E4"/><circle cx="62" cy="38" r="2" fill="#FBF3E4"/>` +
    `<path d="M72 46 Q84 50 82 60 Q76 52 70 50 Z" fill="#D7263D"/>` +
    `<ellipse cx="59" cy="56" rx="7" ry="6.4" fill="#1A1230"/><path d="M26 48 L66 52 M52 50 L74 46" stroke="#1A1230" stroke-width="2"/>` +
    `<path d="M40 72 Q50 78 60 72" stroke="#3B2414" stroke-width="2" fill="none"/></g>`,
  princess:
    bg('princess', '#F27AC0', '#9B1F6A') + `<g clip-path="url(#clip-princess)">` + body('#E9B949', '#FBF3E4') +
    `<circle cx="27" cy="38" r="11" fill="#2A1A14"/><circle cx="73" cy="38" r="11" fill="#2A1A14"/>` +
    face('#8D5A3B', '#E0607E') + smile('#4A1A1A') +
    `<path d="M27 54 Q28 34 50 33 Q72 34 73 54 Q66 42 50 41 Q34 42 27 54 Z" fill="#2A1A14"/>` +
    crown(30, 0.55, '#17A673') + `</g>`,
  lion:
    bg('lion', '#FFD36B', '#C9821A') + `<g clip-path="url(#clip-lion)">` +
    `<circle cx="50" cy="56" r="36" fill="#B5651D"/>` +
    Array.from({ length: 12 }, (_, i) => {
      const a = (i / 12) * Math.PI * 2;
      return `<circle cx="${(50 + Math.cos(a) * 32).toFixed(1)}" cy="${(56 + Math.sin(a) * 32).toFixed(1)}" r="9" fill="#9C4F12"/>`;
    }).join('') +
    `<circle cx="50" cy="58" r="24" fill="#F2B04A"/><circle cx="31" cy="38" r="7" fill="#F2B04A"/><circle cx="69" cy="38" r="7" fill="#F2B04A"/>` +
    `<ellipse cx="41" cy="54" rx="3.2" ry="3.8" fill="#2B1B3D"/><ellipse cx="59" cy="54" rx="3.2" ry="3.8" fill="#2B1B3D"/>` +
    `<circle cx="42.2" cy="52.6" r="1.1" fill="#fff"/><circle cx="60.2" cy="52.6" r="1.1" fill="#fff"/>` +
    `<ellipse cx="50" cy="66" rx="10" ry="7" fill="#FBE3B5"/><path d="M46 62 L54 62 L50 66 Z" fill="#5A2A1A"/>` +
    `<path d="M50 66 L50 69 M45 70 Q50 73 55 70" stroke="#5A2A1A" stroke-width="2" fill="none" stroke-linecap="round"/></g>`,
};

/** SVG markup for an avatar (unknown ids fall back to the king). */
export function avatarSvg(id: string): string {
  const key = ART[id] ? id : 'king';
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" role="img" aria-label="${AVATARS.find((a) => a.id === key)?.name ?? ''}">${ART[key]}</svg>`;
}

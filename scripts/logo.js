#!/usr/bin/env node
// Writes the logo of MatPhoto in its levels of detail (docs/logo and src/MatPhoto/wwwroot/img/logo, the favicon of wwwroot):
//   mark      the full mark (64 x 64): two prints on top of each other, a landscape with the sun on the front one
//   favicon   the small mark (32 x 32): one print, thicker shapes, no second print (it would be mush at 16 px)
//   maskable  the mark without corners, the content inside the safe zone (Android adaptive icons)
//   mono      one colour (currentColor) for places that tint it
//   logo      the mark with the word mark (light surfaces), logo-dark (dark surfaces)
// The PNG icons are made from these files with scripts/icons.ps1 (Edge headless).
//   node scripts/logo.js
const fs = require('fs');
const path = require('path');

const accent = '#B81F8A';
const tint = '#F6D5EA';
const shade = '#7E1560';
const root = path.join(__dirname, '..');

// the inside of the 64 x 64 mark (without its background), so the logo and the maskable icon draw the same shapes
const markShapes = [
  `<rect x="17" y="11" width="36" height="28" rx="4" transform="rotate(-9 35 25)" fill="${tint}"/>`,
  `<rect x="11" y="20" width="40" height="32" rx="4" fill="#fff"/>`,
  `<path d="M15.5 47.5 25 36.5l6.5 7 4.5-4.5 10.5 8.5z" fill="${accent}"/>`,
  `<circle cx="40.5" cy="29" r="4" fill="${shade}"/>`,
].join('');

const files = {
  'matphoto-mark.svg':
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" role="img" aria-label="MatPhoto">\n` +
    `  <rect width="64" height="64" rx="14" fill="${accent}"/>\n  ${markShapes}\n</svg>\n`,
  'matphoto-favicon.svg':
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32" role="img" aria-label="MatPhoto">\n` +
    `  <rect width="32" height="32" rx="7" fill="${accent}"/>\n` +
    `  <rect x="5" y="8" width="22" height="17" rx="2.5" fill="#fff"/>\n` +
    `  <path d="M7.5 22.5 13 16l3.5 4 2-2 5 4.5z" fill="${accent}"/>\n` +
    `  <circle cx="21.5" cy="12.5" r="2.2" fill="${shade}"/>\n</svg>\n`,
  'matphoto-maskable.svg':
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" role="img" aria-label="MatPhoto">\n` +
    `  <rect width="64" height="64" fill="${accent}"/>\n  <g transform="translate(9.6 9.6) scale(0.7)">${markShapes}</g>\n</svg>\n`,
  'matphoto-mono.svg':
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" role="img" aria-label="MatPhoto" fill="currentColor">\n` +
    `  <rect x="17" y="11" width="36" height="28" rx="4" transform="rotate(-9 35 25)" opacity=".45"/>\n` +
    `  <path fill-rule="evenodd" d="M15 20h32a4 4 0 0 1 4 4v24a4 4 0 0 1-4 4H15a4 4 0 0 1-4-4V24a4 4 0 0 1 4-4zM15.5 47.5h31L36 39l-4.5 4.5-6.5-7zM40.5 25a4 4 0 1 0 0 8a4 4 0 1 0 0-8z"/>\n</svg>\n`,
  'matphoto-logo.svg': wordmark('#1F1E1B'),
  'matphoto-logo-dark.svg': wordmark('#F4F2EF'),
};

function wordmark(text) {
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 172 40" width="172" height="40" role="img" aria-label="MatPhoto">\n` +
    `  <rect x="2" y="2" width="36" height="36" rx="8" fill="${accent}"/><g transform="translate(2 2) scale(0.5625)">${markShapes}</g>\n` +
    `  <text x="48" y="28.5" font-family="'Segoe UI','Helvetica Neue',Arial,sans-serif" font-size="22" font-weight="700" letter-spacing="-0.4" fill="${text}">Mat<tspan fill="${text === '#1F1E1B' ? accent : '#E77CC0'}">Photo</tspan></text>\n</svg>\n`;
}

for (const dir of [path.join(root, 'docs', 'logo'), path.join(root, 'src', 'MatPhoto', 'wwwroot', 'img', 'logo')]) {
  fs.mkdirSync(dir, { recursive: true });
  for (const [name, svg] of Object.entries(files)) {
    fs.writeFileSync(path.join(dir, name), svg);
  }
}

fs.writeFileSync(path.join(root, 'src', 'MatPhoto', 'wwwroot', 'favicon.svg'), files['matphoto-favicon.svg']);
console.log(`wrote ${Object.keys(files).length} files into docs/logo and wwwroot/img/logo, and wwwroot/favicon.svg`);

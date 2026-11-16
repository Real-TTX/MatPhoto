// End-to-end walk through MatPhoto with the installed Edge (playwright-core): register, storage, scan, grid, viewer, favourite, list, albums, explore, search,
// duplicates, the import of the inbox (moved into suggested albums), a phone in dark mode. Run against a fresh local stack with fresh sample photos:
//   npm i playwright-core; node tests/e2e/walk.js [folder for the screenshots]
const { chromium } = require('playwright-core');
const fs = require('fs');
const path = require('path');
const BASE = 'http://localhost:4830';
const OUT = process.argv[2] || path.join(__dirname, 'shots');
fs.mkdirSync(OUT, { recursive: true });
const errors = [];
const log = (...a) => console.log(...a);

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'de-DE' });
  const page = await context.newPage();
  page.on('pageerror', e => errors.push('pageerror ' + page.url() + ' ' + e.message));
  page.on('console', m => { if (m.type() === 'error') errors.push('console ' + page.url() + ' ' + m.text()); });
  page.on('response', r => { if (r.status() >= 500) errors.push('HTTP ' + r.status() + ' ' + r.url()); });
  const shot = async (name, p = page) => { await p.screenshot({ path: path.join(OUT, name + '.png') }); log('shot', name); };

  // register the first account (the administrator), or sign in
  await page.goto(BASE + '/Account/Register');
  if (await page.locator('input[name="DisplayName"]').count()) {
    await page.fill('input[name="DisplayName"]', 'Ada Admin');
    await page.fill('input[name="Email"]', 'admin@example.com');
    await page.fill('input[name="Password"]', 'Start12345');
    await Promise.all([page.waitForNavigation(), page.locator('form:has(input[name="Password"]) button[type="submit"]').first().click()]);
  }
  if (page.url().includes('/Account/Login') || page.url().includes('/Account/Register')) {
    await page.goto(BASE + '/Account/Login');
    await page.fill('input[name="Email"]', 'admin@example.com');
    await page.fill('input[name="Password"]', 'Start12345');
    await Promise.all([page.waitForNavigation(), page.locator('form:has(input[name="Password"]) button[type="submit"]').first().click()]);
  }
  log('signed in at', page.url());
  await shot('01-photos-empty');

  // the storage: the sample folder, everybody Editor
  await page.goto(BASE + '/Admin/Storages');
  if (!(await page.content()).includes('Beispiel-NAS')) {
    await page.goto(BASE + '/Admin/Storages/Edit');
    await page.fill('input[name="Name"]', 'Beispiel-NAS');
    await page.locator('input[name="Kind"][value="Local"]').check({ force: true });
    await page.fill('input#RootPath', '/storage/sample');
    await shot('02-storage-edit');
    await Promise.all([page.waitForNavigation(), page.locator('.md-btn-row button[type="submit"]').first().click()]);
    log('after create', page.url());
    await page.locator('input[name="Everyone"][value="Editor"]').check({ force: true });
    await Promise.all([page.waitForNavigation(), page.locator('form[action*="Everyone"] button[type="submit"]').click()]);
  }

  // wait for the scan
  for (let i = 0; i < 90; i++) {
    const r = await page.request.get(BASE + '/live/scans');
    const j = await r.json();
    const s = j.scans.find(x => x.name === 'Beispiel-NAS');
    if (s && s.phase === 'done') { log('scan', s.result); break; }
    if (s && s.phase === 'failed') { errors.push('scan failed ' + s.result); break; }
    await page.waitForTimeout(2000);
  }
  await page.goto(BASE + '/Admin/Scans');
  await shot('03-scans');

  // the timeline
  await page.goto(BASE + '/Photos');
  await page.waitForSelector('.md-tile img', { timeout: 20000 });
  await page.waitForTimeout(1500);
  const tiles = await page.locator('.md-tile').count();
  log('tiles', tiles);
  await shot('04-photos');

  // the viewer: open, next, info, favourite
  await page.locator('.md-tile').first().click();
  await page.waitForSelector('.md-viewer:not([hidden]) .md-viewer__img');
  await page.waitForTimeout(800);
  await page.keyboard.press('ArrowRight');
  await page.waitForTimeout(800);
  await page.keyboard.press('i');
  await page.waitForSelector('.md-viewer__info:not([hidden]) .md-fact');
  await page.waitForTimeout(500);
  await shot('05-viewer-info');
  await page.keyboard.press('f');
  await page.waitForTimeout(500);
  await page.keyboard.press('Escape');
  await page.waitForTimeout(500);
  if (await page.locator('.md-viewer:not([hidden])').count()) errors.push('viewer did not close');

  // select two photos and put them on a new list
  await page.locator('.md-tile').nth(2).click({ modifiers: ['Control'] });
  await page.locator('.md-tile').nth(3).click();
  await page.waitForSelector('[data-selbar]:not([hidden])');
  await shot('06-selection');
  await page.locator('[data-sel-action="list"]').click();

  await page.waitForSelector('dialog[open] #md-newlist');
  await page.fill('#md-newlist', 'Girlande Max Geburtstag');
  await page.locator('dialog[open] [data-new-list] button[type="submit"]').click();
  await page.waitForTimeout(1000);

  // albums, an album, explore, favourites, the list, search, duplicates
  for (const [url, name] of [['/Albums', '07-albums'], ['/Explore', '09-explore'], ['/Favorites', '10-favorites'], ['/Lists', '11-lists'], ['/Search?q=Gardasee', '12-search'], ['/Duplicates', '13-duplicates'], ['/Admin/System?tab=thumbnails', '14-system']]) {
    await page.goto(BASE + url);
    await page.waitForTimeout(1500);
    await shot(name);
  }
  await page.goto(BASE + '/Albums');
  await page.locator('.md-card').first().click();
  await page.waitForSelector('.md-tile img');
  await page.waitForTimeout(800);
  await shot('08-album');

  // the import of the inbox (a folder of the storage)
  await page.goto(BASE + '/Import');
  await page.fill('input[name="SourcePath"]', 'Inbox');
  await page.locator('input#MoveFiles').check({ force: true });
  await page.waitForTimeout(800);
  await shot('15-import');
  await Promise.all([page.waitForNavigation(), page.locator('form[data-import-storage] button[type="submit"]').click()]);
  for (let i = 0; i < 60 && !(await page.locator('.md-group').count()); i++) {
    await page.waitForTimeout(2000);
    if (!(await page.locator('[data-import-status]').count())) await page.reload();
  }
  await page.waitForTimeout(500);
  await shot('16-import-review');
  const groups = await page.locator('.md-group').count();
  if (groups) {
    await Promise.all([page.waitForNavigation(), page.locator('.md-btn-row button[type="submit"]').first().click()]);
    for (let i = 0; i < 60 && !(await page.locator('.mat-alert--success').count()); i++) { await page.waitForTimeout(2000); if (!(await page.locator('[data-import-status]').count())) await page.reload(); }
    await shot('16b-import-done');
    log('import', await page.locator('.mat-alert').first().textContent());
  }
  log('suggested groups', groups, await page.locator('.md-group input[id^="group-name-"]').evaluateAll(n => n.map(x => x.value)));

  // the phone and dark mode
  const phone = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true, locale: 'de-DE', colorScheme: 'dark', storageState: await context.storageState() });
  const p2 = await phone.newPage();
  p2.on('pageerror', e => errors.push('phone pageerror ' + e.message));
  await p2.goto(BASE + '/Photos');
  await p2.waitForSelector('.md-tile img');
  await p2.waitForTimeout(1200);
  await shot('17-phone-photos', p2);
  await p2.locator('.md-tile').first().tap();
  await p2.waitForSelector('.md-viewer:not([hidden]) .md-viewer__img');
  await p2.waitForTimeout(1000);
  await shot('18-phone-viewer', p2);
  await p2.goto(BASE + '/Explore');
  await p2.waitForTimeout(1200);
  await shot('19-phone-explore', p2);

  await browser.close();
  log('ERRORS', errors.length);
  errors.forEach(e => log(' -', e));
})().catch(e => { console.error(e.message); errors.forEach(x => console.log(' -', x)); process.exit(1); });

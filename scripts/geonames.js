#!/usr/bin/env node
// Writes the places of MatPhoto (src/MatPhoto/Resources/Geo) from the dumps of GeoNames (https://download.geonames.org/export/dump/, CC BY 4.0):
//   cities15000.zip (unpacked: cities15000.txt), admin1CodesASCII.txt, countryInfo.txt in one folder.
//   node scripts/geonames.js <folder with the dumps>
// cities.tsv.gz: name, latitude, longitude, country code, region (one town per line); countries.tsv: code, name. The parts of a town (PPLX, e.g. "Mitte" of
// Berlin) are left out: a photo in Berlin is in Berlin.
const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

const source = process.argv[2];
if (!source) { console.error('usage: node scripts/geonames.js <folder with cities15000.txt, admin1CodesASCII.txt, countryInfo.txt>'); process.exit(64); }
const target = path.join(__dirname, '..', 'src', 'MatPhoto', 'Resources', 'Geo');
const lines = file => fs.readFileSync(path.join(source, file), 'utf8').split('\n').filter(l => l.length > 0);

const regions = {};
for (const line of lines('admin1CodesASCII.txt')) {
  const p = line.split('\t');
  regions[p[0]] = p[1];
}

const countries = [];
for (const line of lines('countryInfo.txt')) {
  if (line.startsWith('#')) continue;
  const p = line.split('\t');
  countries.push(p[0] + '\t' + p[4]);
}

const towns = [];
for (const line of lines('cities15000.txt')) {
  const p = line.split('\t');
  if (p[7] === 'PPLX') continue;
  towns.push([p[1], (+p[4]).toFixed(4), (+p[5]).toFixed(4), p[8], regions[p[8] + '.' + p[10]] || ''].join('\t'));
}

fs.mkdirSync(target, { recursive: true });
fs.writeFileSync(path.join(target, 'cities.tsv.gz'), zlib.gzipSync(towns.join('\n') + '\n', { level: 9 }));
fs.writeFileSync(path.join(target, 'countries.tsv'), countries.sort().join('\n') + '\n');
console.log(`${towns.length} towns, ${countries.length} countries written to ${target}`);

// SPDX-License-Identifier: GPL-3.0-only
// Collect the installed runtime dependency licenses, preserving upstream notices.
import { readFile, readdir, mkdir, writeFile, copyFile } from 'node:fs/promises';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const lock = JSON.parse(await readFile(join(root, 'package-lock.json'), 'utf8'));
const packages = [];
for (const [relative, entry] of Object.entries(lock.packages)) {
  if (!relative || entry.dev) continue;
  const directory = join(root, relative);
  const metadata = JSON.parse(await readFile(join(directory, 'package.json'), 'utf8'));
  const names = (await readdir(directory))
    .filter((name) => /^(licen[cs]e|copying|notice)([.-]|$)/i.test(name))
    .sort();
  const texts = [];
  for (const name of names)
    texts.push(`${name}\n\n${(await readFile(join(directory, name), 'utf8')).trim()}`);
  if (!texts.length) {
    const identity = `${metadata.name}@${metadata.version}`;
    if (identity === '@ant-design/icons-svg@4.6.0') {
      texts.push(
        'Upstream LICENSE (npm release gitHead 7f2516ac91226d2b41f93b35cb5197c8d94f7189)\n\n' +
          (
            await readFile(join(root, 'scripts/license-overrides/ant-design-icons-svg-4.6.0.LICENSE'), 'utf8')
          ).trim(),
      );
    } else if (identity === 'is-mobile@5.0.0') {
      const readme = await readFile(join(directory, 'README.md'), 'utf8');
      const start = readme.indexOf('## License');
      if (start < 0) throw new Error('is-mobile license section is missing');
      texts.push('README.md — License section\n\n' + readme.slice(start).trim());
    } else if (identity === 'toggle-selection@1.0.6') {
      texts.push(
        'Upstream declares the MIT license in package.json, but does not supply a separate license or copyright text in this release. The original attribution and package metadata follow. No upstream copyright notice has been invented.\n\npackage.json\n\n' +
          (await readFile(join(directory, 'package.json'), 'utf8')).trim() +
          '\n\nREADME.md\n\n' +
          (await readFile(join(directory, 'README.md'), 'utf8')).trim(),
      );
    } else {
      throw new Error(`Missing upstream license file: ${identity}`);
    }
  }
  const repository = typeof metadata.repository === 'string' ? metadata.repository : metadata.repository?.url;
  packages.push({
    name: metadata.name,
    version: metadata.version,
    license: metadata.license ?? entry.license ?? 'See included license',
    repository: repository ?? '',
    texts,
  });
}
packages.sort((a, b) => a.name.localeCompare(b.name, 'en') || a.version.localeCompare(b.version, 'en'));
const unique = packages.filter(
  (entry, index) =>
    index === 0 ||
    `${entry.name}@${entry.version}` !== `${packages[index - 1].name}@${packages[index - 1].version}`,
);
const summary =
  '# Relay console third-party dependencies\n\nGenerated from package-lock.json by `npm run licenses`. Runtime dependencies only; original license and copyright text are preserved in THIRD_PARTY_LICENSES.txt.\n\n| Package | Version | License |\n| --- | --- | --- |\n' +
  unique
    .map(
      (item) =>
        `| ${item.name} | ${item.version} | ${typeof item.license === 'string' ? item.license : JSON.stringify(item.license)} |`,
    )
    .join('\n') +
  '\n';
const fullText =
  'EmbedDesk Relay Console — Third-party licenses\n\nOriginal console source is licensed GPL-3.0-only. The following dependencies retain their respective upstream licenses.\n\n' +
  unique
    .map(
      (item) =>
        `${'='.repeat(78)}\n${item.name}@${item.version}\nLicense: ${typeof item.license === 'string' ? item.license : JSON.stringify(item.license)}\nSource: ${item.repository}\n\n${item.texts.join('\n\n')}`,
    )
    .join('\n\n') +
  '\n';
await mkdir(join(root, 'licenses'), { recursive: true });
await writeFile(join(root, 'licenses', 'DEPENDENCIES.md'), summary, 'utf8');
await writeFile(join(root, 'licenses', 'THIRD_PARTY_LICENSES.txt'), fullText, 'utf8');
if (process.argv.includes('--dist')) {
  await mkdir(join(root, 'dist', 'licenses'), { recursive: true });
  for (const name of ['DEPENDENCIES.md', 'THIRD_PARTY_LICENSES.txt'])
    await copyFile(join(root, 'licenses', name), join(root, 'dist', 'licenses', name));
  await writeFile(join(root, 'dist', '.gitkeep'), '', 'utf8');
}
console.log(`Collected original license notices for ${unique.length} runtime dependencies.`);

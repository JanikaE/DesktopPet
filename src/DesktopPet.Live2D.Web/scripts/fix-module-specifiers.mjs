import { readdir, readFile, writeFile } from 'node:fs/promises';
import { extname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const outputRoot = fileURLToPath(new URL('../../DesktopPet/Assets/Live2D/Runtime/modules/', import.meta.url));

async function visit(directory) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) await visit(path);
    else if (entry.name.endsWith('.js')) await normalize(path);
  }
}

async function normalize(path) {
  const source = await readFile(path, 'utf8');
  const normalized = source.replace(
    /(\bfrom\s*['"]|\bimport\s*\(\s*['"])([^'"]+)(['"])/g,
    (match, prefix, specifier, suffix) => {
      if (!(specifier.startsWith('.') || specifier.startsWith('@framework/')) || extname(specifier)) return match;
      return `${prefix}${specifier}.js${suffix}`;
    }
  );
  if (normalized !== source) await writeFile(path, normalized, 'utf8');
}

await visit(outputRoot);

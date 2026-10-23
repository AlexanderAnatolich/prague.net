// Renders the React landing page to static HTML at build time.
// The page ships no JavaScript: what crawlers, screen readers and
// browsers with scripts disabled see is the full content.
import { readFile, writeFile, rm } from 'node:fs/promises';
import { fileURLToPath, pathToFileURL } from 'node:url';
import path from 'node:path';

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const dist = path.join(root, 'dist');
const ssrDir = path.join(root, '.prerender');
const indexPath = path.join(dist, 'index.html');

const { render } = await import(pathToFileURL(path.join(ssrDir, 'entry-server.js')).href);
const template = await readFile(indexPath, 'utf8');
const marker = '<!--app-html-->';
if (!template.includes(marker)) throw new Error(`prerender: ${indexPath} has no ${marker} marker`);

await writeFile(indexPath, template.replace(marker, render()));
await rm(ssrDir, { recursive: true, force: true });
console.log('prerender: wrote static HTML into dist/index.html');

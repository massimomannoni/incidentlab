import { execFile, spawn } from 'node:child_process';
import { mkdtemp, rm, writeFile, chmod } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// Kept at tools/dist/common.js after compilation; independent of the caller's cwd.
export const ROOT = resolve(fileURLToPath(new URL('../..', import.meta.url)));
export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };
export type Obj = { [key: string]: Json };
export class LabError extends Error {}
export const requireThat: (ok: unknown, message: string) => asserts ok = (ok, message) => {
  if (!ok) throw new LabError(message);
};
export function object(value: unknown): Obj {
  requireThat(value !== null && typeof value === 'object' && !Array.isArray(value), 'Expected a JSON object (values withheld).');
  return value as Obj;
}
export const array = (value: Json | undefined): Obj[] => {
  requireThat(Array.isArray(value), 'Expected a JSON array (values withheld).');
  return value.map(object);
};
export const str = (value: Json | undefined): string => {
  requireThat(typeof value === 'string', 'Expected a string (values withheld).');
  return value;
};
export const num = (value: Json | undefined): number => {
  requireThat(typeof value === 'number' && Number.isFinite(value), 'Expected a finite number.');
  return value;
};
export const sleep = (ms: number, signal?: AbortSignal) => new Promise<void>((done, reject) => {
  if (signal?.aborted) return reject(new LabError('Operation cancelled.'));
  const abort = () => { clearTimeout(timer); reject(new LabError('Operation cancelled.')); };
  const timer = setTimeout(() => { signal?.removeEventListener('abort', abort); done(); }, ms);
  signal?.addEventListener('abort', abort, { once: true });
});
export interface ProcessResult { code: number; stdout: string; stderr: string }
export type Execute = (file: string, args: string[], cwd?: string, env?: NodeJS.ProcessEnv, signal?: AbortSignal) => Promise<ProcessResult>;
export const execute: Execute = (file, args, cwd = ROOT, env = process.env, signal) => new Promise((done, reject) => {
  execFile(file, args, { cwd, env, signal, maxBuffer: 16 * 1024 * 1024, encoding: 'utf8' }, (error, stdout, stderr) => {
    if (error && typeof error.code !== 'number') return reject(new LabError(`Cannot start ${file}; check installation and permissions.`));
    done({ code: error ? Number(error.code) : 0, stdout, stderr });
  });
});
export async function interactive(file: string, args: string[], env = process.env, signal?: AbortSignal): Promise<void> {
  await new Promise<void>((done, reject) => {
    const child = spawn(file, args, { stdio: 'inherit', env, signal });
    child.once('error', () => reject(new LabError(`Cannot start ${file}.`)));
    child.once('exit', code => code === 0 ? done() : reject(new LabError(`${file} ended unsuccessfully.`)));
  });
}
export async function temporary<T>(prefix: string, action: (directory: string) => Promise<T>): Promise<T> {
  const directory = await mkdtemp(join(tmpdir(), prefix));
  await chmod(directory, 0o700);
  try { return await action(directory); }
  finally { await rm(directory, { recursive: true, force: true }); } // Exact directory created by this invocation only.
}
export async function secureJson(path: string, value: Obj): Promise<void> {
  await writeFile(path, JSON.stringify(value), { mode: 0o600, flag: 'wx' });
}
export const shellQuote = (text: string): string => "'" + text.replaceAll("'", "'\\''") + "'";

export interface Options { command: string; values: Record<string, string | boolean | string[]> }
export function parse(args: string[]): Options {
  const command = args[0] ?? 'help';
  const values: Options['values'] = {};
  const flags = new Set(['apply', 'with-codex', 'manual-ai', 'judge', 'confirm-ai', 'allow-eu-inference']);
  for (let i = 1; i < args.length; i++) {
    requireThat(args[i].startsWith('--'), `Unexpected argument: ${args[i]}`);
    const key = args[i].slice(2);
    requireThat(!Object.hasOwn(values, key), `Duplicate option: --${key}`);
    if (flags.has(key)) values[key] = true;
    else if (key === 'scenarios') {
      const scenarios: string[] = [];
      while (args[i + 1] && !args[i + 1].startsWith('--')) scenarios.push(args[++i]);
      requireThat(scenarios.length, '--scenarios requires at least one name.');
      values[key] = scenarios;
    } else {
      requireThat(args[i + 1] && !args[i + 1].startsWith('--'), `--${key} requires a value.`);
      values[key] = args[++i];
    }
  }
  return { command, values };
}
export function only(options: Options['values'], allowed: string[]): void {
  for (const key of Object.keys(options)) requireThat(allowed.includes(key), `Unknown option: --${key}`);
}

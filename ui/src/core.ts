// The page's only way to the core (SPEC §38): Tauri commands, typed here once.
import { invoke } from '@tauri-apps/api/core';

export interface AppInfo {
  version: string;
  release: boolean;
  dataFolder: string;
}

export const appInfo = () => invoke<AppInfo>('app_info');

/** Tells the app the page has started (a `--self-test` run exits on it). */
export const pageReady = () => invoke<void>('page_ready');

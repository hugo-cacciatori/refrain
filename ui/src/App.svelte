<script lang="ts">
  // R0's page: proves the path from the page to the core. The Search window replaces it in R5 (SPEC §38).
  import { getCurrentWindow } from '@tauri-apps/api/window';
  import { appInfo, pageReady, type AppInfo } from './core';
  import { viewFor } from './view';

  const view = viewFor(getCurrentWindow().label);
  let info = $state<AppInfo>();
  let failure = $state<string>();

  $effect(() => {
    appInfo()
      .then((answer) => {
        info = answer;
        return pageReady();
      })
      .catch((e: unknown) => (failure = String(e)));
  });
</script>

<main data-view={view}>
  <h1>Refrain{info && !info.release ? ' Dev' : ''}</h1>
  {#if failure}
    <p role="alert">The page couldn't reach the app: {failure}</p>
  {:else if info}
    <p>{info.version}, data in {info.dataFolder}</p>
  {/if}
</main>

<style>
  main {
    font-family: system-ui, sans-serif;
    padding: 2rem;
  }
</style>

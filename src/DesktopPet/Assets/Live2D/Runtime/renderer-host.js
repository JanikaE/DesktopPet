(async () => {
  'use strict';

  const send = value => window.chrome.webview.postMessage(value);
  const fail = error => send({ type: 'fault', message: String(error && error.message ? error.message : error) });

  try {
    const encoded = new URLSearchParams(location.search).get('config');
    if (!encoded) throw new Error('缺少 Live2D 渲染配置。');
    const config = JSON.parse(new TextDecoder().decode(Uint8Array.from(atob(encoded), char => char.charCodeAt(0))));
    await import('./modules/src/adapter.js');
    if (!window.desktopPetCubism || typeof window.desktopPetCubism.create !== 'function') {
      throw new Error('Live2D 适配器未提供 desktopPetCubism.create。');
    }

    const canvas = document.getElementById('pet');
    const renderer = window.desktopPetCubism.create(canvas, config);
    window.chrome.webview.addEventListener('message', event => {
      const message = event.data;
      if (message && message.type === 'state' && typeof renderer.setState === 'function') renderer.setState(message.state);
      if (message && message.type === 'pointer' && Number.isFinite(message.x) && Number.isFinite(message.y) && typeof renderer.setPointer === 'function') {
        renderer.setPointer(message.x, message.y);
      }
    });
    Promise.resolve(renderer.ready).then(() => send({ type: 'ready' })).catch(fail);
    addEventListener('beforeunload', () => { if (typeof renderer.dispose === 'function') renderer.dispose(); }, { once: true });
  } catch (error) {
    fail(error);
  }
})();

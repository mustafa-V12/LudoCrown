import '@fontsource/baloo-2/600.css';
import '@fontsource/baloo-2/700.css';
import '@fontsource/baloo-2/800.css';
import './styles.css';
import { App } from './ui/app';

const root = document.getElementById('app');
if (root) new App(root, import.meta.env.BASE_URL).start();

// Offline support after the first visit (production builds only).
if ('serviceWorker' in navigator && import.meta.env.PROD) {
  window.addEventListener('load', () => {
    navigator.serviceWorker.register(`${import.meta.env.BASE_URL}sw.js`).catch(() => undefined);
  });
}

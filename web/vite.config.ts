import { defineConfig } from 'vitest/config';

// GitHub Pages serves the site from /<repository>/. Override with BASE_PATH for other hosts (e.g. BASE_PATH=/ ).
export default defineConfig({
  base: process.env.BASE_PATH ?? '/LudoCrown/',
  build: { target: 'es2020', sourcemap: false },
  test: { include: ['test/**/*.test.ts'] },
});

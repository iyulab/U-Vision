// @ts-check
import { defineConfig } from 'astro/config';

export default defineConfig({
  site: 'https://vision.u-platform.kr',
  output: 'static',
  trailingSlash: 'always',
  i18n: {
    defaultLocale: 'ko',
    locales: ['ko', 'en'],
    routing: { prefixDefaultLocale: false },
  },
});

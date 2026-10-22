import type { APIRoute } from 'astro';
import { PATHS, SITE_URL } from '../copy';

// Both language versions, each naming the other (hreflang), as search engines read a sitemap.
export const GET: APIRoute = () => {
  const url = (path: string) => new URL(path, SITE_URL).href;
  const entries = Object.values(PATHS).flatMap(page =>
    (['ko', 'en'] as const).map(
      locale => `  <url>
    <loc>${url(page[locale])}</loc>
    <xhtml:link rel="alternate" hreflang="ko" href="${url(page.ko)}"/>
    <xhtml:link rel="alternate" hreflang="en" href="${url(page.en)}"/>
  </url>`
    )
  );
  const body = `<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml">
${entries.join('\n')}
</urlset>
`;
  return new Response(body, { headers: { 'Content-Type': 'application/xml' } });
};

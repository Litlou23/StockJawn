/**
 * ensure-next-static.js
 *
 * @netlify/plugin-nextjs expects .next/static to exist after build.
 * Next.js 16 may not create it when there are no static assets to emit
 * (no public/ folder, no static CSS/JS chunks in certain webpack configs).
 * Without it, the plugin's publishStaticDir() fails on:
 *   rename(.netlify/static, .next)
 * because .netlify/static was never created (copyStaticAssets skips it
 * when neither public/ nor .next/static/ exists).
 *
 * This script ensures .next/static exists after every build.
 */

const fs = require('fs');
const path = require('path');

const staticDir = path.join(__dirname, '..', '.next', 'static');

if (!fs.existsSync(staticDir)) {
  fs.mkdirSync(staticDir, { recursive: true });
  fs.writeFileSync(path.join(staticDir, '.keep'), '');
  console.log('[ensure-next-static] Created .next/static/.keep');
} else {
  console.log('[ensure-next-static] .next/static already exists');
}

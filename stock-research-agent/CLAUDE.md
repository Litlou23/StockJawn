@AGENTS.md

## Project Notes

- **Restart reminder**: When making changes to API routes, server services, or anything under `services/` or `app/api/`, remind Lou to restart the dev server (`npm run dev`) so changes go live.
- **No mock data**: This is a live system. Do not use mock fallbacks. If a data source fails, return empty results with honest status — never inject fake data.
- **RSS/intake is the live data source**: The information intake pipeline (`services/informationIntake/`) fetches real RSS feeds. The learning analysis pulls from this live data, not just manually-entered outcomes.

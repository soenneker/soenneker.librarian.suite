# Librarian website

The public site at https://librarian.soenneker.com. Built with .NET 10 Razor components and Soenneker.Quark.Suite, rendered to static HTML for Cloudflare Workers Assets. No .NET server or WebAssembly download is required in production.

## Export

```powershell
./src/Soenneker.Librarian.Website/scripts/Export-Site.ps1
```

The script publishes the project, briefly starts a local rendering server, exports all pages, checks local links and anchors, and stops the server in a `finally` block. Output is in `src/Soenneker.Librarian.Website/out` (ignored by Git).

## Deployment

Google Analytics uses the `librarian.soenneker.com` property and `Librarian website` web stream, with measurement ID `G-MKGXJ98JER`. The shared document head loads the tag on every page; `wwwroot/js/analytics.js` initializes it, and `wwwroot/_headers` allows the Google tag and Analytics endpoints.

`.github/workflows/website.yml` builds pull requests and deploys pushes to main or manual runs on main. Configure the repository secret `CLOUDFLARE_API_TOKEN` with Workers Scripts Edit for the deployment account, plus Zone Read and Workers Routes Edit for the soenneker.com zone. No credentials belong in source control.

Worker: `soenneker-librarian-website`. Custom domain: `librarian.soenneker.com`. Wrangler configuration is the source of truth for asset handling and routes. Workers.dev and preview URLs are disabled.

Add new page routes to `scripts/Export-Site.ps1` and `wwwroot/sitemap.xml`. Keep product claims and examples aligned with the suite README and `docs/`. Benchmark numbers describe warm in-memory reads only.

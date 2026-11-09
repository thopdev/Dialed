# Branding assets (#97)

- Sources: `tools/branding/icon.svg` (app icon) and `icon-maskable.svg` (bean inside the 80 % safe zone).
- `python3 tools/branding/generate.py` (needs `rsvg-convert`) rewrites the icons, the favicon and 34 portrait iOS splash screens (17 device sizes, light and dark) in `src/Dialed.Web/wwwroot`, and prints the `apple-touch-startup-image` links for `index.html`. Run it after changing the SVGs and paste the links over the old ones.
- Theme colours: light `#6F4E37` (espresso), dark `#1E1713`; splash background `#F4ECE0` / `#1E1713`.
- Splash screens are excluded from the service-worker cache (iOS reads them at launch only).
- `InstallMetadataTests` checks that everything the page and manifest reference exists.
- Landscape splash screens are not generated; iOS shows a plain background there.
- Check on a phone: Add to Home Screen, then open from the home screen; the icon, name "Dialed" and splash should show (user-side).

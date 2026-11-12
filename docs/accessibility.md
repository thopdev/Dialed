# Accessibility and dark mode (#49)

## What is automated

- `AccessibilityTests` (bUnit) renders every main screen and fails on a form field without a label, a button or link without a name, an image without `alt`, or a stray icon that is neither hidden nor labeled. Add a screen to the list when you add one. Screens that only show something once there is data (the bean page with its journey, taste profile and recipe offer, the grind drift note, gear upkeep, tasting note suggestions, the notification switches) are audited with data in place, because an empty screen hides most of their controls.
- `DesignSystemTests` keeps colours out of our own CSS: every colour comes from the Fluent `--color*` variables, which Fluent generates for both light and dark from the brand key colour. So dark mode and contrast follow the Fluent theme (its brand palettes are designed for WCAG AA text contrast).

## What the CSS does

- Text sizes are in `rem`, and the root follows the iOS text size (`-apple-system-body`, Dynamic Type); the tab bar label is 0.75rem minimum, the badge and "photo missing" text scale too.
- Touch targets are at least 44 px (`--dialed-touch-target`).
- A visible `:focus-visible` ring (keyboard, switch control), `prefers-reduced-motion` switches animations off, and `forced-colors` keeps borders visible.

## Manual pass (needs a phone, cannot run in CI)

Do this on an iPhone with the installed PWA, in light and dark mode. Tick per screen: Home, Brew (picker, log, timer), Beans, Bags, Recipes, History, Equipment, Vendors, Inventory, People, Settings, Onboarding.

1. **VoiceOver** (Settings > Accessibility > VoiceOver): swipe through the screen. Every control is read with a sensible name and role; headings are found with the rotor; the brew timer announces the current step; errors are announced (`role="alert"`).
2. **Large text** (Settings > Display > Text Size, drag to the maximum, and Accessibility > Larger Text): nothing is clipped or overlaps, the tab bar labels wrap, forms stay usable.
3. **Dark mode** (Control Center or Settings > Display): no white flashes, text readable, photos and charts still clear. `/dev/gallery` shows all components in one place.
4. **Contrast**: spot-check muted text (hints, tab labels, badges) with the Accessibility Inspector or a contrast checker; the target is 4.5:1 for text, 3:1 for icons and borders.
5. **Reduce Motion** on: the timer and skeletons do not animate.

Findings go in a new issue per screen.

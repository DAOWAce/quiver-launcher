# Quiver Launcher 3.4.5

## More reliable startup version checks

- Show saved latest-version information as your library loads. Versions awaiting verification stay visible with **(pending check)** instead of going blank; the hint clears after a successful check.
- Use the shared platform index from enabled catalog sources to refresh version information for matching apps, including manually added repositories. Only apps without matching information verified within the last 24 hours need an individual repository check at startup.
- Preserve preferred release selections and use the most recently verified information. Downloads still fetch release details when needed.
- Check library versions independently of catalog-list refreshes, so a slow or unavailable catalog does not stop library checks. Allow up to five seconds for the shared index before falling back to repositories.
- Retry temporary connection, timeout, and server failures once, checking only the affected apps. Manual update checks still contact repositories directly; scheduled checks keep their existing behavior.

## Library loading and app actions

- Show an animated indicator and **Loading your library…** while the local library loads, then replace it with your apps without waiting for network refreshes.
- Keep the loading animation on the render thread so it can continue while app cards are being prepared. Preserve the existing card loading and scrolling behavior.
- Prioritize **Download**, **Update Now**, and **Change Version** over queued background release requests while respecting active requests and provider rate limits.
- Preserve active download and installation states during startup checks, and cancel pending startup work when Quiver closes.

## Platform detection fixes

- Exclude notices and source archives from platform detection and download choices. Files such as `KartPad-v0.5.0-notices.zip` no longer incorrectly imply Windows or Linux support.
- Apply the fix to existing cached and published asset metadata without clearing caches. Ordinary app ZIP packages remain supported.

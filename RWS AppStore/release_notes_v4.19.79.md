# RWS App Store Manager – v4.19.79.0

**Version number:** `4.19.79.0`
**Minimum studio version:** `18.0`
**Maximum studio version:** `18.9`
**Checksum:** `0479b5c7de3512df2258a1e243791cd9f526e470cb1e931149e66d89e4ccc923`

---

## Changelog

### Fixed
- **At 150% Windows display scaling, the leading "S" of "Source code available for security audit" in the About dialog was clipped behind the shield emoji.** Cause: the link's `Location.X` was `leftPad + 30`, but the shield emoji's AutoSize width grew at high DPI past that 30 px gap, so the wider emoji's bounding box ate into the link.
- Fix at [`AboutDialog.cs`](src/Supervertaler.Trados/Controls/AboutDialog.cs): position the link dynamically from `shieldLabel.Right + 6` instead of a fixed 30 px gap.
- **The two font-size buttons at the top of the TermLens panel had the same problem the AI Assistant chat header had** before 4.19.74: design said "A+" and "A−" with a 2-point font-size difference, but at low DPI the +/− glyphs collapsed to thin strokes and both buttons looked like plain "A".
- Fix at [`TermLensControl.cs`](src/Supervertaler.Trados/Controls/TermLensControl.cs): same redesign as the chat header in 4.19.74 – drop the +/− glyphs, use a big bold "A" (11pt) for increase and a small regular "A" (7pt) for decrease, and add explicit "Increase TermLens font size" / "Decrease TermLens font size" tooltips on hover.

For the full changelog, see: https://github.com/Supervertaler/Supervertaler-for-Trados/releases
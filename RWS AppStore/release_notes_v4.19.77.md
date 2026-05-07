# RWS App Store Manager – v4.19.77.0

**Version number:** `4.19.77.0`
**Minimum studio version:** `18.0`
**Maximum studio version:** `18.9`
**Checksum:** `c69f4ef3577260d9cc1d32cb57e44a2d93c0ea2ab6c139a827816a18b6d8be06`

---

## Changelog

### Fixed
- **At 150% Windows display scaling, the "Batch size" and "Surrounding segments" numeric inputs in Settings → AI Settings rendered with so little room for digits that the value was hard to read** – the system-drawn spinner buttons (which don't scale identically with the rest of the control) ate most of the visual width, leaving only a few pixels for the actual number. The "Surrounding segments" NUD also overlapped its own label, because the label's AutoSize width grew at high DPI but the NUD's x position was fixed at 210 px.
- Fix at [`AiSettingsPanel.cs`](src/Supervertaler.Trados/Controls/AiSettingsPanel.cs): bump both NUDs from `Width = 60` to `Width = 80` so the AutoScaleMode.Dpi pass produces a visibly comfortable text area at any scaling. Also position `_nudSurroundingSegments` dynamically from `_lblSurroundingSegments.Right + 8` instead of the fixed x=210 column the other rows use, so the wider label at high DPI doesn't push it.
- The other two NUDs in this panel (`_nudOllamaTimeout` at width 75, `_nudMaxSegments` at width 80) were already wide enough; no change there.

For the full changelog, see: https://github.com/Supervertaler/Supervertaler-for-Trados/releases
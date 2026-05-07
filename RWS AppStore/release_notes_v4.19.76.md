# RWS App Store Manager – v4.19.76.0

**Version number:** `4.19.76.0`
**Minimum studio version:** `18.0`
**Maximum studio version:** `18.9`
**Checksum:** `e4a7ae16db645ded5dd4c0e6e2a6cf0920200c1a7c384b4c1873690da50500b7`

---

## Changelog

### Fixed
- **Earlier today's fix only covered the `BatchTranslateControl` (Batch Operations panel) and the `AiAssistantControl` (Chat header). The other 23 dialogs and panels in the plugin – Settings, AI Settings, Prompts, Termbase Editor, AI Proofreader Reports, About, Setup, every term-add / merge / preview pop-up – still relied on WinForms' default font-based autoscaling, which doesn't reliably propagate through plugin UserControls hosted inside Trados.** Nobody had complained yet, but layout would have squished at 125% / 150% / 200% Windows display scaling on every one of those surfaces.
- Fix: each of those 23 forms / UserControls now sets `AutoScaleMode = AutoScaleMode.Dpi` in its constructor or `BuildUI()` method. This activates WinForms' DPI-based scaling pass, which scales control sizes and positions by the raw `currentDpi / designDpi` ratio – the same mechanism `TermPopup` has used successfully for the term-popup. The two surfaces with their own UiScale-driven layout (`AiAssistantControl`, `BatchTranslateControl`) keep `AutoScaleMode = None` so they don't double-scale on top of UiScale.
- No functional behaviour change at 100% Windows scaling. At 125% / 150% / 200% scaling, plugin dialogs now scale uniformly with the rest of Trados Studio's UI instead of staying at 100% and squishing.

For the full changelog, see: https://github.com/Supervertaler/Supervertaler-for-Trados/releases
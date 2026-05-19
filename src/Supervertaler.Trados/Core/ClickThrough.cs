using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Workaround for the Studio 2026 first-click-eaten bug on dock-pane buttons.
    ///
    /// On Trados Studio 2026, the WPF-based docking host swallows WM_LBUTTONDOWN
    /// during the ~600ms it spends activating a previously-inactive dock pane.
    /// While the activation runs, the click message is consumed at the host
    /// level — neither MouseDown nor Click fires on the hosted WinForms button.
    /// The user would have to click a second time to get the action.
    ///
    /// Diagnostic message logging confirmed that GotFocus on the button DOES
    /// fire reliably during the eaten-click sequence, and at that moment the
    /// user is still physically holding the left mouse button down on the
    /// button (they're in the middle of the click that triggered activation).
    ///
    /// <see cref="Attach"/> wires a GotFocus handler that fires the supplied
    /// action when:
    ///   1. The left mouse button is currently held (= user is mid-click).
    ///   2. The cursor is over the button (= they're clicking us specifically).
    ///
    /// Subsequent clicks on the same button don't re-fire because the button
    /// already has focus — no new GotFocus event. Programmatic focus changes
    /// (Tab, Focus()) don't trigger this because the user isn't holding the mouse.
    ///
    /// An earlier attempt also did pre-emptive btn.Focus() on MouseEnter to
    /// pre-activate the pane. It worked for clicks but triggered
    /// ScrollControlIntoView on the focused button, which yanked the
    /// TermLens scroll position to the top and clipped the segment content.
    /// Reverted — the GotFocus fallback alone is sufficient.
    /// </summary>
    internal static class ClickThrough
    {
        /// <summary>
        /// Wire <paramref name="action"/> to fire when <paramref name="btn"/>
        /// gains focus via a left-click while the dock pane was inactive
        /// (which is the only path that suppresses the normal Click event on
        /// Studio 2026). The button's regular Click handler should still be
        /// wired separately — this is additive for the inactive-pane case.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        public static void Attach(Control btn, Action action)
        {
            if (btn == null || action == null) return;

            // Pre-emptive activation: on MouseEnter, call Win32 SetFocus on the
            // button's native HWND. This nudges the WPF docking host to
            // activate our pane before the user actually clicks, so the click
            // hits an already-active pane and the normal Click event fires.
            //
            // We use Win32 SetFocus directly (rather than btn.Focus()) because
            // Control.Focus goes through WinForms' ContainerControl logic
            // which calls ScrollControlIntoView on the focused control. That
            // scrolls the segment display to the buttons at the top of the
            // pane, clipping the rest of the content. The native SetFocus
            // bypasses that — the WPF host still gets the activation but
            // WinForms doesn't observe a managed focus change.
            btn.MouseEnter += (s, e) =>
            {
                if (btn.IsHandleCreated && !btn.Focused)
                {
                    try { SetFocus(btn.Handle); } catch { }
                }
            };

            // GotFocus synthesis: even with pre-emptive activation, the very
            // first click after a long inactive period can still be lost if
            // the user clicks faster than the activation completes. GotFocus
            // fires during the eaten-click sequence and the mouse is still
            // held, so we can recover the action.
            btn.GotFocus += (s, e) =>
            {
                if (Control.MouseButtons != MouseButtons.Left) return;
                var clientPos = btn.PointToClient(Cursor.Position);
                if (!btn.ClientRectangle.Contains(clientPos)) return;
                action();
            };
        }

        /// <summary>
        /// Overload for buttons whose existing Click handler is an
        /// <see cref="EventHandler"/> (method reference). The handler is
        /// invoked with the button as sender and EventArgs.Empty.
        /// </summary>
        public static void Attach(Control btn, EventHandler handler)
        {
            if (btn == null || handler == null) return;
            Attach(btn, () => handler(btn, EventArgs.Empty));
        }
    }
}

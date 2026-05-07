using System;
using System.Drawing;
using System.Windows.Forms;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Global UI scale factor for Supervertaler controls.
    ///
    /// There are two layers of scaling:
    ///
    /// <list type="number">
    ///   <item>
    ///     <description>
    ///     <see cref="SystemScale"/> – auto-detected from Windows DPI on
    ///     plugin startup. 1.0 at 100% scaling, 1.25 at 125%, 1.5 at 150%
    ///     and so on. This is what fixes the layout breakage Daniel saw on
    ///     a 2560×1440 display at 150% Windows scaling.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///     <see cref="UserFactor"/> – an extra multiplier the user can
    ///     dial in via TermLens settings (<see cref="Settings.TermLensSettings.UiScaleFactor"/>).
    ///     Useful for users who want bigger text on top of whatever
    ///     Windows is doing. Defaults to 1.0.
    ///     </description>
    ///   </item>
    /// </list>
    ///
    /// <see cref="Pixels"/> and <see cref="FontSize"/> multiply by
    /// <see cref="EffectiveScale"/> = <see cref="SystemScale"/> ×
    /// <see cref="UserFactor"/>.
    ///
    /// <para>
    /// IMPORTANT: any UserControl that relies on UiScale-driven dimensions
    /// must set <c>AutoScaleMode = AutoScaleMode.None</c>, otherwise
    /// WinForms' built-in font-based autoscaling kicks in on top of
    /// UiScale and you get double scaling. UiScale owns all DPI-related
    /// sizing for these controls.
    /// </para>
    /// </summary>
    public static class UiScale
    {
        private static float _systemScale = 1.0f;
        private static float _userFactor = 1.0f;

        /// <summary>
        /// System DPI scale, auto-detected at startup. 1.0 = 100%,
        /// 1.5 = 150%, etc. Read-only outside of <see cref="SeedSystemScale"/>.
        /// </summary>
        public static float SystemScale => _systemScale;

        /// <summary>
        /// User-configurable extra scale on top of the system DPI.
        /// Driven by the TermLens settings slider.
        /// </summary>
        public static float UserFactor
        {
            get => _userFactor;
            set => _userFactor = value > 0 ? value : 1.0f;
        }

        /// <summary>
        /// Combined scale factor: <see cref="SystemScale"/> × <see cref="UserFactor"/>.
        /// </summary>
        public static float EffectiveScale => _systemScale * _userFactor;

        /// <summary>
        /// Backwards-compat alias. Old code referred to <c>UiScale.Factor</c>
        /// for the user preference; that's now <see cref="UserFactor"/>.
        /// Reads from / writes to UserFactor.
        /// </summary>
        public static float Factor
        {
            get => _userFactor;
            set => UserFactor = value;
        }

        /// <summary>
        /// Detect the current system DPI and use it as <see cref="SystemScale"/>.
        /// Call once at plugin startup. Safe to call again on DPI change events
        /// (e.g. when the user moves Trados to a different monitor).
        /// </summary>
        public static void SeedSystemScale()
        {
            try
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    _systemScale = g.DpiX / 96f;
                }
                if (_systemScale <= 0f || _systemScale > 4f)
                    _systemScale = 1.0f;
            }
            catch
            {
                _systemScale = 1.0f;
            }
        }

        /// <summary>
        /// Returns a font size scaled by <see cref="EffectiveScale"/>.
        /// </summary>
        public static float FontSize(float baseSize) => baseSize * EffectiveScale;

        /// <summary>
        /// Returns a pixel dimension scaled by <see cref="EffectiveScale"/>,
        /// rounded to the nearest int.
        /// </summary>
        public static int Pixels(int basePixels) => (int)Math.Round(basePixels * EffectiveScale);
    }
}

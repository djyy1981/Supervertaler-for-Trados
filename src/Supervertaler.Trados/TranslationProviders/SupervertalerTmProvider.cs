using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.TranslationProviders
{
    /// <summary>
    /// A Trados <see cref="ITranslationProvider"/> backed by a single
    /// "Bridged" TM in Supervertaler Workbench's shared SQLite database
    /// (<c>supervertaler.db</c>).
    ///
    /// Phase 2 of the Shared TM work (Trados issue #31). Workbench v1.10.212+
    /// adds a per-TM <c>bridged_to_trados</c> flag in its TMs tab; only TMs
    /// the user has explicitly ticked appear in this provider's URI space
    /// (<c>supervertaler-tm:///&lt;tm_id&gt;</c>).
    ///
    /// v1 scope (this release):
    ///   * Read-only. <see cref="IsReadOnly"/> = true. Add/Update/Delete on
    ///     the language direction return failure / are NotSupported.
    ///   * Exact source-text match search via SQLite hash index. 100% only.
    ///   * Concordance search (source side AND target side) via the
    ///     existing FTS5 index Workbench maintains.
    ///   * No fuzzy matching. SupportsFuzzySearch = false. The provider
    ///     returns *only* 100% matches; sub-100 matches will be added when
    ///     a proper Levenshtein-on-candidates scorer ships in Phase 3.
    ///
    /// The implementation is deliberately minimal – just enough surface area
    /// to let Trados Studio attach the provider, pull exact matches into the
    /// TM-results pane, and run concordance searches in the SuperSearch tab
    /// / Studio's built-in Concordance window. Editing flows through the
    /// user's normal SDLTM(s); Bridged TMs are pure reference look-ups for
    /// v1.
    /// </summary>
    public class SupervertalerTmProvider : ITranslationProvider
    {
        // The "supervertaler-tm:" URI scheme used to identify this provider
        // type to Trados Studio. The TM's stable string id lives in the URI
        // path component, e.g. supervertaler-tm:///BEIJER.
        public const string ProviderScheme = "supervertaler-tm";
        public const string TranslationProviderScheme = ProviderScheme;

        private readonly Uri _uri;
        private readonly TmInfo _tmInfo;
        private readonly string _dbPath;

        /// <summary>
        /// Construct from a parsed URI + a snapshot of TM metadata captured
        /// at construction time. The metadata is allowed to be null when the
        /// TM has since been un-bridged in Workbench – the provider still
        /// loads (so the project's `.sdlproj` reference isn't broken) but
        /// reports unavailability via <see cref="StatusInfo"/>.
        /// </summary>
        internal SupervertalerTmProvider(Uri uri, TmInfo tmInfo, string dbPath)
        {
            _uri = uri ?? throw new ArgumentNullException(nameof(uri));
            _tmInfo = tmInfo;
            _dbPath = dbPath;
        }

        // ─── Identity ─────────────────────────────────────────────────

        public Uri Uri => _uri;

        public string Name =>
            _tmInfo != null
                ? "Supervertaler TM: " + _tmInfo.Name
                : "Supervertaler TM (not found)";

        public TranslationMethod TranslationMethod => TranslationMethod.TranslationMemory;

        public bool IsReadOnly => true;

        public ProviderStatusInfo StatusInfo
        {
            get
            {
                if (_tmInfo == null)
                    return new ProviderStatusInfo(false,
                        "The Supervertaler TM '" + ExtractTmIdFromUri(_uri) +
                        "' is no longer marked as Bridged in Supervertaler Workbench.");
                if (string.IsNullOrEmpty(_dbPath) || !File.Exists(_dbPath))
                    return new ProviderStatusInfo(false,
                        "Supervertaler database not found at " + (_dbPath ?? "(unset)") +
                        ". Open Supervertaler Workbench at least once to create it.");
                return new ProviderStatusInfo(true,
                    "Bridged from Supervertaler Workbench (" + _tmInfo.EntryCount + " entries)");
            }
        }

        public void RefreshStatusInfo()
        {
            // StatusInfo is computed on demand from _tmInfo + disk state, so
            // there's nothing to refresh proactively. Workbench's writer side
            // owns the freshness story.
        }

        // ─── State serialisation ──────────────────────────────────────

        // The Trados project file persists this string between sessions.
        // We have no per-provider configuration today, so this is empty.
        public string SerializeState() => string.Empty;
        public void LoadState(string translationProviderState) { /* no-op */ }

        // ─── Capability flags ─────────────────────────────────────────
        //
        // Conservative set for v1 – everything not implemented yet is OFF
        // so Studio doesn't surface UI promising features that aren't there.

        public bool SupportsTaggedInput => false;
        public bool SupportsScoring => true;          // we return ScoringResult on hits
        public bool SupportsSearchForTranslationUnits => true;
        public bool SupportsMultipleResults => true;
        public bool SupportsFilters => false;
        public bool SupportsPenalties => false;
        public bool SupportsStructureContext => false;
        public bool SupportsDocumentSearches => false;
        public bool SupportsUpdate => false;          // Phase 3
        public bool SupportsPlaceables => false;
        public bool SupportsTranslation => true;
        public bool SupportsFuzzySearch => false;     // Phase 3
        public bool SupportsConcordanceSearch => true;
        public bool SupportsSourceConcordanceSearch => true;
        public bool SupportsTargetConcordanceSearch => true;
        public bool SupportsWordCounts => false;

        // ─── Language direction routing ──────────────────────────────

        public bool SupportsLanguageDirection(LanguagePair languageDirection)
        {
            if (_tmInfo == null || languageDirection == null) return false;
            // Match on the leading sub-language tag only ("en" matches "en-US",
            // "en-GB" etc.) – Workbench's locale storage is loose (sometimes a
            // bare ISO code, sometimes a full BCP-47) so a strict match would
            // reject legitimate pairs constantly.
            return CulturesCompatible(_tmInfo.SourceLang, languageDirection.SourceCulture.Name)
                && CulturesCompatible(_tmInfo.TargetLang, languageDirection.TargetCulture.Name);
        }

        public ITranslationProviderLanguageDirection GetLanguageDirection(LanguagePair languageDirection)
        {
            return new SupervertalerTmLanguageDirection(this, languageDirection, _tmInfo, _dbPath);
        }

        // ─── Helpers ──────────────────────────────────────────────────

        internal static string ExtractTmIdFromUri(Uri uri)
        {
            if (uri == null) return null;
            // supervertaler-tm:///BEIJER → "BEIJER"
            // supervertaler-tm:///BRANTS%20(PROJ) → "BRANTS (PROJ)"
            var path = uri.AbsolutePath;
            if (string.IsNullOrEmpty(path) || path == "/") return null;
            return Uri.UnescapeDataString(path.TrimStart('/'));
        }

        internal static Uri BuildUriForTm(string tmId)
        {
            if (string.IsNullOrEmpty(tmId))
                throw new ArgumentException("tmId is required", nameof(tmId));
            return new Uri(ProviderScheme + ":///" + Uri.EscapeDataString(tmId));
        }

        /// <summary>
        /// Loose locale compatibility: two locale strings match if their
        /// primary sub-language tag (the part before the first '-') is the
        /// same, case-insensitive. So "en" matches "en-US", "nl-NL" matches
        /// "nl-BE", etc. Workbench stores locales in mixed formats so a
        /// strict equality check would reject many legitimate attachments.
        /// </summary>
        internal static bool CulturesCompatible(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            var primaryA = PrimaryLangTag(a);
            var primaryB = PrimaryLangTag(b);
            return string.Equals(primaryA, primaryB, StringComparison.OrdinalIgnoreCase);
        }

        private static string PrimaryLangTag(string code)
        {
            if (string.IsNullOrEmpty(code)) return string.Empty;
            var dash = code.IndexOfAny(new[] { '-', '_' });
            return dash < 0 ? code : code.Substring(0, dash);
        }
    }
}

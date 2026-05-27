using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sdl.Core.Globalization;
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
            TmBridgeLog.Info(
                "Provider ctor: uri=" + uri +
                ", tmInfo=" + (tmInfo == null ? "null" : (tmInfo.Name + " (" + tmInfo.TmId + ", " + tmInfo.SourceLang + "->" + tmInfo.TargetLang + ", " + tmInfo.EntryCount + " TUs)")) +
                ", dbPath=" + (dbPath ?? "(null)"));
        }

        // ─── Identity ─────────────────────────────────────────────────

        public Uri Uri
        {
            get
            {
                TmBridgeLog.Info("Provider.Uri get => " + (_uri == null ? "(null)" : _uri.ToString()));
                return _uri;
            }
        }

        public string Name
        {
            get
            {
                var n = _tmInfo != null
                    ? "Supervertaler TM: " + _tmInfo.Name
                    : "Supervertaler TM (not found)";
                TmBridgeLog.Info("Provider.Name get => " + n);
                return n;
            }
        }

        public TranslationMethod TranslationMethod
        {
            get
            {
                TmBridgeLog.Info("Provider.TranslationMethod get => TranslationMemory");
                return TranslationMethod.TranslationMemory;
            }
        }

        public bool IsReadOnly
        {
            get
            {
                TmBridgeLog.Info("Provider.IsReadOnly get => true");
                return true;
            }
        }

        public ProviderStatusInfo StatusInfo
        {
            get
            {
                try
                {
                    if (_tmInfo == null)
                    {
                        TmBridgeLog.Info("Provider.StatusInfo get => Unavailable (no TmInfo)");
                        return new ProviderStatusInfo(false,
                            "The Supervertaler TM '" + ExtractTmIdFromUri(_uri) +
                            "' is no longer marked as Bridged in Supervertaler Workbench.");
                    }
                    if (string.IsNullOrEmpty(_dbPath) || !File.Exists(_dbPath))
                    {
                        TmBridgeLog.Info("Provider.StatusInfo get => Unavailable (db missing: " + (_dbPath ?? "(null)") + ")");
                        return new ProviderStatusInfo(false,
                            "Supervertaler database not found at " + (_dbPath ?? "(unset)") +
                            ". Open Supervertaler Workbench at least once to create it.");
                    }
                    TmBridgeLog.Info("Provider.StatusInfo get => Available (" + _tmInfo.EntryCount + " entries)");
                    return new ProviderStatusInfo(true,
                        "Bridged from Supervertaler Workbench (" + _tmInfo.EntryCount + " entries)");
                }
                catch (Exception ex)
                {
                    TmBridgeLog.Error("Provider.StatusInfo get: threw", ex);
                    return new ProviderStatusInfo(false, "Error: " + ex.Message);
                }
            }
        }

        public void RefreshStatusInfo()
        {
            TmBridgeLog.Info("Provider.RefreshStatusInfo()");
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
        //
        // v4.20.29: SupportsTranslation is now FALSE. SDK convention: a
        // `true` value advertises this as an *automated translation*
        // (MT-style) provider, prompting Studio to invoke a translation-
        // engine code path that doesn't exist for a TM lookup provider.
        // That mismatch was producing the "Object reference not set to an
        // instance of an object" failures observed in v4.20.28 – Studio
        // would call SupportsLanguageDirection / GetLanguageDirection in
        // a tight loop without ever reaching SearchSegment.
        //
        // Every getter is also instrumented so future regressions can be
        // diagnosed from %TEMP%\supervertaler-tm-bridge.log alone.

        public bool SupportsTaggedInput { get { TmBridgeLog.Info("Provider.SupportsTaggedInput => false"); return false; } }
        public bool SupportsScoring { get { TmBridgeLog.Info("Provider.SupportsScoring => true"); return true; } }
        public bool SupportsSearchForTranslationUnits { get { TmBridgeLog.Info("Provider.SupportsSearchForTranslationUnits => true"); return true; } }
        public bool SupportsMultipleResults { get { TmBridgeLog.Info("Provider.SupportsMultipleResults => true"); return true; } }
        public bool SupportsFilters { get { TmBridgeLog.Info("Provider.SupportsFilters => false"); return false; } }
        public bool SupportsPenalties { get { TmBridgeLog.Info("Provider.SupportsPenalties => false"); return false; } }
        public bool SupportsStructureContext { get { TmBridgeLog.Info("Provider.SupportsStructureContext => false"); return false; } }
        public bool SupportsDocumentSearches { get { TmBridgeLog.Info("Provider.SupportsDocumentSearches => false"); return false; } }
        public bool SupportsUpdate { get { TmBridgeLog.Info("Provider.SupportsUpdate => false"); return false; } }
        public bool SupportsPlaceables { get { TmBridgeLog.Info("Provider.SupportsPlaceables => false"); return false; } }
        public bool SupportsTranslation { get { TmBridgeLog.Info("Provider.SupportsTranslation => false"); return false; } }
        public bool SupportsFuzzySearch { get { TmBridgeLog.Info("Provider.SupportsFuzzySearch => false"); return false; } }
        public bool SupportsConcordanceSearch { get { TmBridgeLog.Info("Provider.SupportsConcordanceSearch => true"); return true; } }
        public bool SupportsSourceConcordanceSearch { get { TmBridgeLog.Info("Provider.SupportsSourceConcordanceSearch => true"); return true; } }
        public bool SupportsTargetConcordanceSearch { get { TmBridgeLog.Info("Provider.SupportsTargetConcordanceSearch => true"); return true; } }
        public bool SupportsWordCounts { get { TmBridgeLog.Info("Provider.SupportsWordCounts => false"); return false; } }

        // ─── Language direction routing ──────────────────────────────

        public bool SupportsLanguageDirection(LanguagePair languageDirection)
        {
            try
            {
                if (_tmInfo == null)
                {
                    TmBridgeLog.Warn("SupportsLanguageDirection: _tmInfo is null");
                    return false;
                }
                if (languageDirection == null)
                {
                    TmBridgeLog.Warn("SupportsLanguageDirection: languageDirection is null");
                    return false;
                }

                // CultureCode might be a reference type in some Trados SDK
                // versions; guard against null SourceCulture/TargetCulture
                // before calling .Name on them.
                string src = SafeGetCultureName(languageDirection.SourceCulture);
                string tgt = SafeGetCultureName(languageDirection.TargetCulture);

                bool ok = CulturesCompatible(_tmInfo.SourceLang, src)
                       && CulturesCompatible(_tmInfo.TargetLang, tgt);

                TmBridgeLog.Info(
                    "SupportsLanguageDirection: TM=" + _tmInfo.Name +
                    " (" + _tmInfo.SourceLang + "->" + _tmInfo.TargetLang +
                    ") vs project (" + src + "->" + tgt + ") => " + ok);
                return ok;
            }
            catch (Exception ex)
            {
                TmBridgeLog.Error("SupportsLanguageDirection: threw", ex);
                return false;
            }
        }

        public ITranslationProviderLanguageDirection GetLanguageDirection(LanguagePair languageDirection)
        {
            TmBridgeLog.Info(
                "GetLanguageDirection: TM=" + (_tmInfo != null ? _tmInfo.Name : "(null TmInfo)") +
                ", langPair=" + (languageDirection == null ? "(null)"
                    : SafeGetCultureName(languageDirection.SourceCulture) + "->" + SafeGetCultureName(languageDirection.TargetCulture)));
            return new SupervertalerTmLanguageDirection(this, languageDirection, _tmInfo, _dbPath);
        }

        private static string SafeGetCultureName(CultureCode c)
        {
            try
            {
                // CultureCode is a struct in current Trados SDKs but treat
                // it defensively: .Name could theoretically be null.
                return c.Name ?? "(empty)";
            }
            catch
            {
                return "(error)";
            }
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

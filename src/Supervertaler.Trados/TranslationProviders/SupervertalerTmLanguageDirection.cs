using System;
using System.Collections.Generic;
using Sdl.Core.Globalization;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.TranslationProviders
{
    /// <summary>
    /// Per-language-pair search engine for <see cref="SupervertalerTmProvider"/>.
    ///
    /// Two real entry points in v1:
    ///   * <see cref="SearchSegment"/> – exact source-text lookup, returns
    ///     100% matches only. Studio's TM-results pane shows these as plain
    ///     "100%" hits alongside any other providers attached to the project.
    ///   * <see cref="SearchText"/> – concordance lookup, returns up to
    ///     <see cref="MaxConcordanceResults"/> rows where the source text
    ///     contains the query substring. Source-side by default; Studio's
    ///     Concordance window calls this for both directions and uses the
    ///     <see cref="SearchSettings.Mode"/> field to indicate which.
    ///
    /// Every Add/Update/Delete method on the interface throws
    /// <see cref="NotSupportedException"/> because v1 is read-only
    /// (<see cref="ITranslationProvider.IsReadOnly"/> = true). Phase 3 of
    /// the Shared TM work will implement write-back; until then any caller
    /// that ignores <c>IsReadOnly</c> and tries to write should fail loudly.
    /// </summary>
    public class SupervertalerTmLanguageDirection : ITranslationProviderLanguageDirection
    {
        // Concordance search caps. Trados' concordance window already paginates,
        // so we don't need to return everything – capping at 30 keeps the
        // SQL fast and the round-trip light.
        private const int MaxConcordanceResults = 30;

        // Exact-match cap. Multiple TUs CAN share an identical source text
        // (different translations of the same string). We want all of them
        // surfaced; 10 is generous and avoids accidental denial-of-service
        // on a pathological TM.
        private const int MaxExactResults = 10;

        private readonly SupervertalerTmProvider _provider;
        private readonly LanguagePair _languagePair;
        private readonly TmInfo _tmInfo;
        private readonly string _dbPath;

        internal SupervertalerTmLanguageDirection(
            SupervertalerTmProvider provider,
            LanguagePair languagePair,
            TmInfo tmInfo,
            string dbPath)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _languagePair = languagePair ?? throw new ArgumentNullException(nameof(languagePair));
            _tmInfo = tmInfo;
            _dbPath = dbPath;
        }

        // ─── Identity ─────────────────────────────────────────────────

        public ITranslationProvider TranslationProvider => _provider;
        public CultureCode SourceLanguage => _languagePair.SourceCulture;
        public CultureCode TargetLanguage => _languagePair.TargetCulture;
        public bool CanReverseLanguageDirection => false;

        // ─── Search: exact (SearchSegment) ────────────────────────────

        public SearchResults SearchSegment(SearchSettings settings, Segment segment)
        {
            // Defensive: Studio occasionally passes null segments in
            // exploratory pre-flight calls. Returning an empty SearchResults
            // is the documented correct behaviour – throwing here causes
            // Trados to surface "An error has occurred while using the
            // translation provider" and disable the provider for the
            // session.
            var results = new SearchResults();
            try
            {
                if (segment != null)
                    results.SourceSegment = segment.Duplicate();
            }
            catch (Exception ex)
            {
                TmBridgeLog.Error("SearchSegment: failed to Duplicate source segment", ex);
            }

            if (_tmInfo == null)
            {
                TmBridgeLog.Warn("SearchSegment called on provider with no TmInfo (TM no longer bridged?)");
                return results;
            }
            if (segment == null)
            {
                TmBridgeLog.Warn("SearchSegment called with null segment");
                return results;
            }

            string queryText;
            try
            {
                queryText = segment.ToPlain() ?? string.Empty;
            }
            catch (Exception ex)
            {
                TmBridgeLog.Error("SearchSegment: segment.ToPlain() threw", ex);
                return results;
            }
            if (string.IsNullOrEmpty(queryText)) return results;

            try
            {
                using (var reader = new TmReader(_dbPath))
                {
                    if (!reader.Open())
                    {
                        TmBridgeLog.Warn("SearchSegment: TmReader.Open() failed: " + (reader.LastError ?? "(no message)"));
                        return results;
                    }

                    var matches = reader.SearchExact(_tmInfo.TmId, queryText, MaxExactResults);
                    foreach (var m in matches)
                    {
                        var sr = TryBuildSearchResult(m);
                        if (sr != null) results.Add(sr);
                    }
                }
            }
            catch (Exception ex)
            {
                TmBridgeLog.Error(
                    "SearchSegment: lookup against TM '" + _tmInfo.TmId +
                    "' failed for query '" + Truncate(queryText, 80) + "'", ex);
            }

            return results;
        }

        public SearchResults[] SearchSegments(SearchSettings settings, Segment[] segments)
        {
            if (segments == null) return new SearchResults[0];
            var output = new SearchResults[segments.Length];
            for (int i = 0; i < segments.Length; i++)
                output[i] = SearchSegment(settings, segments[i]);
            return output;
        }

        public SearchResults[] SearchSegmentsMasked(SearchSettings settings, Segment[] segments, bool[] mask)
        {
            if (segments == null) return new SearchResults[0];
            var output = new SearchResults[segments.Length];
            for (int i = 0; i < segments.Length; i++)
            {
                if (mask != null && i < mask.Length && !mask[i])
                {
                    output[i] = new SearchResults();
                    continue;
                }
                output[i] = SearchSegment(settings, segments[i]);
            }
            return output;
        }

        // ─── Search: concordance (SearchText) ─────────────────────────

        public SearchResults SearchText(SearchSettings settings, string segment)
        {
            var results = new SearchResults();
            if (_tmInfo == null || string.IsNullOrEmpty(segment)) return results;

            // Direction comes from settings.Mode – source-side or target-side
            // concordance. Default to source if Studio passes something we
            // don't recognise.
            var searchTarget = settings != null
                && settings.Mode == SearchMode.ConcordanceSearch
                && false; // SearchMode doesn't distinguish src/tgt at this level
            // Studio actually issues two separate concordance calls (one with
            // each direction); we cover both by also looking at TargetConcordance
            // in higher-level callers. For SearchText specifically, the convention
            // is that the caller has already picked the direction it wants, so
            // we run the source-side search here and let SearchTranslationUnit
            // handle the target variant.

            try
            {
                using (var reader = new TmReader(_dbPath))
                {
                    if (!reader.Open())
                    {
                        TmBridgeLog.Warn("SearchText: TmReader.Open() failed: " + (reader.LastError ?? "(no message)"));
                        return results;
                    }
                    var matches = reader.SearchConcordance(
                        _tmInfo.TmId, segment, searchTarget: false, MaxConcordanceResults);
                    foreach (var m in matches)
                    {
                        var sr = TryBuildSearchResult(m);
                        if (sr != null) results.Add(sr);
                    }
                }
            }
            catch (Exception ex)
            {
                TmBridgeLog.Error("SearchText: failed for query '" + Truncate(segment, 80) + "'", ex);
            }

            return results;
        }

        public SearchResults SearchTranslationUnit(SearchSettings settings, TranslationUnit translationUnit)
        {
            // Studio uses this for target-side concordance when the user
            // searches for text that should appear in the *target* of an
            // existing TU. Decide which side to search by checking which
            // segment the caller populated.
            var results = new SearchResults();
            if (_tmInfo == null || translationUnit == null) return results;

            string query = null;
            bool searchTarget = false;
            if (translationUnit.TargetSegment != null && !translationUnit.TargetSegment.IsEmpty)
            {
                query = translationUnit.TargetSegment.ToPlain();
                searchTarget = true;
            }
            else if (translationUnit.SourceSegment != null && !translationUnit.SourceSegment.IsEmpty)
            {
                query = translationUnit.SourceSegment.ToPlain();
                searchTarget = false;
            }
            if (string.IsNullOrEmpty(query)) return results;

            try
            {
                using (var reader = new TmReader(_dbPath))
                {
                    if (!reader.Open())
                    {
                        TmBridgeLog.Warn("SearchTranslationUnit: TmReader.Open() failed: " + (reader.LastError ?? "(no message)"));
                        return results;
                    }
                    var matches = reader.SearchConcordance(
                        _tmInfo.TmId, query, searchTarget, MaxConcordanceResults);
                    foreach (var m in matches)
                    {
                        var sr = TryBuildSearchResult(m);
                        if (sr != null) results.Add(sr);
                    }
                }
            }
            catch (Exception ex)
            {
                TmBridgeLog.Error("SearchTranslationUnit: failed for query '" + Truncate(query, 80) + "'", ex);
            }

            return results;
        }

        public SearchResults[] SearchTranslationUnits(SearchSettings settings, TranslationUnit[] translationUnits)
        {
            if (translationUnits == null) return new SearchResults[0];
            var output = new SearchResults[translationUnits.Length];
            for (int i = 0; i < translationUnits.Length; i++)
                output[i] = SearchTranslationUnit(settings, translationUnits[i]);
            return output;
        }

        public SearchResults[] SearchTranslationUnitsMasked(SearchSettings settings, TranslationUnit[] translationUnits, bool[] mask)
        {
            if (translationUnits == null) return new SearchResults[0];
            var output = new SearchResults[translationUnits.Length];
            for (int i = 0; i < translationUnits.Length; i++)
            {
                if (mask != null && i < mask.Length && !mask[i])
                {
                    output[i] = new SearchResults();
                    continue;
                }
                output[i] = SearchTranslationUnit(settings, translationUnits[i]);
            }
            return output;
        }

        // ─── Write API (Phase 3 – currently all throw) ────────────────

        // v4.20.27: write methods used to throw NotSupportedException to
        // make any consumer that ignored IsReadOnly = true fail loudly,
        // but Trados Studio's batch-tasks pipeline (notably "Update Main
        // Translation Memories") calls these speculatively even when
        // IsReadOnly is reported true – and the thrown exception bubbles
        // up to the user as a generic "provider error". Returning a safe
        // empty result instead is the documented well-behaved pattern.
        public ImportResult AddTranslationUnit(TranslationUnit translationUnit, ImportSettings settings)
            => SafeNotSupportedResult();

        public ImportResult[] AddTranslationUnits(TranslationUnit[] translationUnits, ImportSettings settings)
            => SafeNotSupportedResults(translationUnits);

        public ImportResult[] AddOrUpdateTranslationUnits(TranslationUnit[] translationUnits, int[] previousTranslationHashes, ImportSettings settings)
            => SafeNotSupportedResults(translationUnits);

        public ImportResult[] AddTranslationUnitsMasked(TranslationUnit[] translationUnits, ImportSettings settings, bool[] mask)
            => SafeNotSupportedResults(translationUnits);

        public ImportResult[] AddOrUpdateTranslationUnitsMasked(TranslationUnit[] translationUnits, int[] previousTranslationHashes, ImportSettings settings, bool[] mask)
            => SafeNotSupportedResults(translationUnits);

        public ImportResult UpdateTranslationUnit(TranslationUnit translationUnit)
            => SafeNotSupportedResult();

        public ImportResult[] UpdateTranslationUnits(TranslationUnit[] translationUnits)
            => SafeNotSupportedResults(translationUnits);

        private static ImportResult SafeNotSupportedResult()
        {
            // ImportResult has no explicit "not supported" status; an empty
            // (default-constructed) one signals "no rows applied" without
            // throwing. Trados treats it as a no-op.
            return new ImportResult();
        }

        private static ImportResult[] SafeNotSupportedResults(TranslationUnit[] tus)
        {
            var len = tus != null ? tus.Length : 0;
            var arr = new ImportResult[len];
            for (int i = 0; i < len; i++) arr[i] = new ImportResult();
            return arr;
        }

        // ─── Helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Wraps <see cref="BuildTranslationUnit"/> + <see cref="SearchResult"/>
        /// construction in a try/catch that logs the failure and returns null.
        /// Callers add to the SearchResults list only when this returns non-null,
        /// so one bad row never poisons a whole result batch.
        /// </summary>
        private Sdl.LanguagePlatform.TranslationMemory.SearchResult TryBuildSearchResult(BridgedTu m)
        {
            try
            {
                var tu = BuildTranslationUnit(m);
                if (tu == null) return null;
                // Fully-qualify SearchResult: Supervertaler.Trados.Core has
                // its own SearchResult that would otherwise win the
                // unqualified-name lookup. Match is read-only on
                // ScoringResult – computed from BaseScore minus penalties,
                // so setting BaseScore = 100 with no penalties yields 100%.
                return new Sdl.LanguagePlatform.TranslationMemory.SearchResult(tu)
                {
                    ScoringResult = new ScoringResult { BaseScore = 100 },
                };
            }
            catch (Exception ex)
            {
                TmBridgeLog.Error("TryBuildSearchResult: failed for TU id=" + m.Id, ex);
                return null;
            }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        /// <summary>
        /// Builds a Trados <see cref="TranslationUnit"/> from a raw row
        /// pulled out of Supervertaler's <c>translation_units</c> table.
        /// We only carry over the bare minimum that Studio's UI uses:
        /// source text, target text, system fields (creator + dates). No
        /// custom field values, no contexts – those live on Trados-native
        /// TMs and aren't represented in Workbench's schema.
        /// </summary>
        private TranslationUnit BuildTranslationUnit(BridgedTu m)
        {
            // CultureCode is a value type – it's not nullable, but it CAN
            // be the default/empty value. Trados accepts that; the Segment
            // ctor just stores it. We log if we hit an empty culture so we
            // know our SupportsLanguageDirection plumbing is off.
            if (SourceLanguage.Name == null || TargetLanguage.Name == null)
            {
                TmBridgeLog.Warn(
                    "BuildTranslationUnit: empty culture on language pair " +
                    "(src=" + SourceLanguage.Name + ", tgt=" + TargetLanguage.Name + ")");
            }

            var src = new Segment(SourceLanguage);
            src.Add(m.SourceText ?? string.Empty);

            var tgt = new Segment(TargetLanguage);
            tgt.Add(m.TargetText ?? string.Empty);

            var tu = new TranslationUnit(src, tgt)
            {
                Origin = TranslationUnitOrigin.TM,
                OriginSystem = "Supervertaler",
            };

            // Best-effort system field population – Workbench stores dates
            // as ISO strings; Trados expects DateTime. Parse permissively.
            try
            {
                if (tu.SystemFields != null)
                {
                    if (!string.IsNullOrEmpty(m.CreatedBy))
                        tu.SystemFields.CreationUser = m.CreatedBy;
                    DateTime parsed;
                    if (!string.IsNullOrEmpty(m.CreatedDate) &&
                        DateTime.TryParse(m.CreatedDate, out parsed))
                        tu.SystemFields.CreationDate = parsed;
                    if (!string.IsNullOrEmpty(m.ModifiedDate) &&
                        DateTime.TryParse(m.ModifiedDate, out parsed))
                        tu.SystemFields.ChangeDate = parsed;
                    tu.SystemFields.UseCount = (int)Math.Min(int.MaxValue, m.UsageCount);
                }
            }
            catch
            {
                // SystemFields can be picky about its setters across Trados
                // versions; missing metadata is cosmetic, never fatal.
            }

            return tu;
        }
    }
}

using System;
using System.Collections.Generic;
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
            var results = new SearchResults { SourceSegment = segment.Duplicate() };
            if (_tmInfo == null) return results;

            var queryText = segment != null ? segment.ToPlain() : null;
            if (string.IsNullOrEmpty(queryText)) return results;

            try
            {
                using (var reader = new TmReader(_dbPath))
                {
                    if (!reader.Open()) return results;

                    var matches = reader.SearchExact(_tmInfo.TmId, queryText, MaxExactResults);
                    foreach (var m in matches)
                    {
                        var tu = BuildTranslationUnit(m);
                        var sr = new SearchResult(tu)
                        {
                            ScoringResult = new ScoringResult { Match = 100, BaseScore = 100 },
                        };
                        results.Add(sr);
                    }
                }
            }
            catch
            {
                // Search must never throw – Studio's TM pane treats a thrown
                // exception as a hard provider failure and stops calling the
                // provider for the rest of the session. Return whatever we
                // collected (possibly empty) instead.
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
                    if (!reader.Open()) return results;
                    var matches = reader.SearchConcordance(
                        _tmInfo.TmId, segment, searchTarget: false, MaxConcordanceResults);
                    foreach (var m in matches)
                    {
                        var tu = BuildTranslationUnit(m);
                        var sr = new SearchResult(tu)
                        {
                            ScoringResult = new ScoringResult { Match = 100, BaseScore = 100 },
                        };
                        results.Add(sr);
                    }
                }
            }
            catch { /* swallow – see SearchSegment */ }

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
                    if (!reader.Open()) return results;
                    var matches = reader.SearchConcordance(
                        _tmInfo.TmId, query, searchTarget, MaxConcordanceResults);
                    foreach (var m in matches)
                    {
                        var tu = BuildTranslationUnit(m);
                        var sr = new SearchResult(tu)
                        {
                            ScoringResult = new ScoringResult { Match = 100, BaseScore = 100 },
                        };
                        results.Add(sr);
                    }
                }
            }
            catch { /* swallow */ }

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

        public ImportResult AddTranslationUnit(TranslationUnit translationUnit, ImportSettings settings)
            => throw new NotSupportedException("Supervertaler TM bridge is read-only in v1 (Phase 2). Write-back lands in Phase 3.");

        public ImportResult[] AddTranslationUnits(TranslationUnit[] translationUnits, ImportSettings settings)
            => throw new NotSupportedException("Supervertaler TM bridge is read-only in v1 (Phase 2). Write-back lands in Phase 3.");

        public ImportResult[] AddOrUpdateTranslationUnits(TranslationUnit[] translationUnits, int[] previousTranslationHashes, ImportSettings settings)
            => throw new NotSupportedException("Supervertaler TM bridge is read-only in v1 (Phase 2). Write-back lands in Phase 3.");

        public ImportResult[] AddTranslationUnitsMasked(TranslationUnit[] translationUnits, ImportSettings settings, bool[] mask)
            => throw new NotSupportedException("Supervertaler TM bridge is read-only in v1 (Phase 2). Write-back lands in Phase 3.");

        public ImportResult[] AddOrUpdateTranslationUnitsMasked(TranslationUnit[] translationUnits, int[] previousTranslationHashes, ImportSettings settings, bool[] mask)
            => throw new NotSupportedException("Supervertaler TM bridge is read-only in v1 (Phase 2). Write-back lands in Phase 3.");

        public ImportResult UpdateTranslationUnit(TranslationUnit translationUnit)
            => throw new NotSupportedException("Supervertaler TM bridge is read-only in v1 (Phase 2). Write-back lands in Phase 3.");

        public ImportResult[] UpdateTranslationUnits(TranslationUnit[] translationUnits)
            => throw new NotSupportedException("Supervertaler TM bridge is read-only in v1 (Phase 2). Write-back lands in Phase 3.");

        // ─── Helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Builds a Trados <see cref="TranslationUnit"/> from a raw row
        /// pulled out of Supervertaler's <c>translation_units</c> table.
        /// We only carry over the bare minimum that Studio's UI uses:
        /// source text, target text, system fields (creator + dates). No
        /// custom field values, no contexts – those live on Trados-native
        /// TMs and aren't represented in Workbench's schema.
        /// </summary>
        private TranslationUnit BuildTranslationUnit(TmMatch m)
        {
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

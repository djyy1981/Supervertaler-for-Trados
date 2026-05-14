using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Searches a Trados project's attached file-based translation memories the
    /// way Studio's built-in Concordance does, but folds the hits into
    /// SuperSearch's result list so file results and TM results can be shown
    /// together.
    ///
    /// The TM API does a fuzzy concordance pass; hits are then post-filtered
    /// with <see cref="XliffSearcher.QueryMatches"/> so the Aa / .* / Word
    /// options apply to TM results exactly as they do to file results.
    /// Server-based (GroupShare) TMs are not searched in this version — only
    /// file-based <c>.sdltm</c>.
    /// </summary>
    public static class TmSearcher
    {
        /// <summary>
        /// Finds the file-based <c>.sdltm</c> memories attached to the project
        /// that contains <paramref name="anyProjectFilePath"/>: the TM provider
        /// URIs declared in the project's <c>.sdlproj</c>, plus any <c>.sdltm</c>
        /// in the project's <c>Tm</c> subfolder.
        /// </summary>
        public static List<string> FindProjectTms(string anyProjectFilePath)
        {
            var tms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(anyProjectFilePath)) return new List<string>();

            // Walk up to the project root (directory containing a .sdlproj).
            var dir = Path.GetDirectoryName(anyProjectFilePath);
            string projPath = null;
            while (!string.IsNullOrEmpty(dir))
            {
                try
                {
                    var found = Directory.GetFiles(dir, "*.sdlproj", SearchOption.TopDirectoryOnly);
                    if (found.Length > 0) { projPath = found[0]; break; }
                }
                catch { /* permission denied */ }

                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
            if (projPath == null) return new List<string>();

            var projDir = Path.GetDirectoryName(projPath);

            // 1. TM URIs declared in the .sdlproj
            try
            {
                var projDoc = XDocument.Load(projPath);
                var tmUris = projDoc.Descendants()
                    .Where(e => e.Name.LocalName == "MainTranslationProviderItem"
                             || e.Name.LocalName == "ProjectTranslationProviderItem")
                    .Select(e => e.Attribute("Uri")?.Value)
                    .Where(u => u != null && u.IndexOf(".sdltm", StringComparison.OrdinalIgnoreCase) >= 0);

                foreach (var uri in tmUris)
                {
                    var path = ResolveTmUri(uri, projDir);
                    if (path != null && File.Exists(path)) tms.Add(path);
                }
            }
            catch { /* unreadable .sdlproj */ }

            // 2. Any .sdltm in the project's Tm subfolder
            try
            {
                var tmSubDir = Path.Combine(projDir, "Tm");
                if (Directory.Exists(tmSubDir))
                {
                    foreach (var f in Directory.GetFiles(tmSubDir, "*.sdltm", SearchOption.AllDirectories))
                    {
                        try { tms.Add(Path.GetFullPath(f)); } catch { tms.Add(f); }
                    }
                }
            }
            catch { }

            return tms.OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string ResolveTmUri(string uri, string projectDir)
        {
            if (string.IsNullOrEmpty(uri)) return null;

            var path = uri;
            if (path.StartsWith("sdltm.file:///")) path = path.Substring("sdltm.file:///".Length);
            else if (path.StartsWith("file:///")) path = path.Substring("file:///".Length);

            try { path = Uri.UnescapeDataString(path); } catch { }

            if (!Path.IsPathRooted(path) && !string.IsNullOrEmpty(projectDir))
                path = Path.Combine(projectDir, path);

            try { return Path.GetFullPath(path); } catch { return path; }
        }

        /// <summary>
        /// Runs a concordance search across the given file-based TMs and returns
        /// matching entries as <see cref="SearchResult"/>s tagged
        /// <see cref="ResultKind.TmEntry"/>. Source-only / target-only / both is
        /// honoured via the TM API's source and target concordance modes; the
        /// fuzzy hits are then post-filtered against the exact query options.
        /// </summary>
        public static List<SearchResult> Search(
            List<string> tmFiles,
            string query,
            SearchScope scope,
            bool caseSensitive,
            bool useRegex,
            bool wholeWord,
            Action<int, int> progress,
            CancellationToken ct)
        {
            var results = new List<SearchResult>();
            if (string.IsNullOrEmpty(query) || tmFiles == null || tmFiles.Count == 0)
                return results;

            var modes = new List<SearchMode>();
            if (scope == SearchScope.SourceOnly || scope == SearchScope.SourceAndTarget)
                modes.Add(SearchMode.ConcordanceSearch);
            if (scope == SearchScope.TargetOnly || scope == SearchScope.SourceAndTarget)
                modes.Add(SearchMode.TargetConcordanceSearch);

            int total = tmFiles.Count;
            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(i, total);

                var tmPath = tmFiles[i];
                var tmName = Path.GetFileNameWithoutExtension(tmPath);

                try
                {
                    var tm = new FileBasedTranslationMemory(tmPath);
                    var ld = tm.LanguageDirection;
                    if (ld == null) continue;

                    // A TU can come back on both the source- and target-side
                    // passes; dedupe on the source/target text pair.
                    var seen = new HashSet<string>();

                    foreach (var mode in modes)
                    {
                        ct.ThrowIfCancellationRequested();

                        var settings = new SearchSettings
                        {
                            Mode = mode,
                            MaxResults = 500,
                            MinScore = 30
                        };

                        SearchResults sr;
                        try { sr = ld.SearchText(settings, query); }
                        catch { continue; }
                        if (sr?.Results == null) continue;

                        foreach (var r in sr.Results)
                        {
                            ct.ThrowIfCancellationRequested();

                            var tu = r.MemoryTranslationUnit;
                            if (tu == null) continue;

                            var sourceText = tu.SourceSegment?.ToPlain() ?? "";
                            var targetText = tu.TargetSegment?.ToPlain() ?? "";

                            // Post-filter the fuzzy concordance hit against the
                            // user's exact case / regex / whole-word options.
                            bool matches = false;
                            if (scope == SearchScope.SourceOnly || scope == SearchScope.SourceAndTarget)
                                matches = XliffSearcher.QueryMatches(sourceText, query, caseSensitive, useRegex, wholeWord);
                            if (!matches && (scope == SearchScope.TargetOnly || scope == SearchScope.SourceAndTarget))
                                matches = XliffSearcher.QueryMatches(targetText, query, caseSensitive, useRegex, wholeWord);
                            if (!matches) continue;

                            if (!seen.Add(sourceText + "" + targetText)) continue;

                            results.Add(new SearchResult
                            {
                                Kind = ResultKind.TmEntry,
                                FilePath = tmPath,
                                FileName = tmName,
                                ParagraphUnitId = null,
                                SegmentId = null,
                                SegmentNumber = 0,
                                SourceText = sourceText,
                                TargetText = targetText,
                                MatchScore = r.ScoringResult?.Match ?? 0,
                                Status = "TM"
                            });
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* skip a TM that can't be opened (locked, server-based, corrupt) */ }
            }

            progress?.Invoke(total, total);
            return results;
        }
    }
}

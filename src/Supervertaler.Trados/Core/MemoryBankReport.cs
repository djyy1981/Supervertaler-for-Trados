using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Generates a human-readable overview of a memory bank from its lightweight
    /// frontmatter index (<see cref="MemoryBankReader.GetIndexSnapshot"/>). The
    /// output is a single self-contained HTML file – no external assets – with a
    /// dashboard, data-quality flags, a terminology-conflict list, a searchable /
    /// sortable terminology table, and a recent-changes list.
    ///
    /// Pure metadata: it reads only frontmatter (already in the index), never full
    /// article bodies, so it stays cheap even on large banks.
    /// </summary>
    public static class MemoryBankReport
    {
        private const int StaleDays = 365;

        // Status values that mark a note as incomplete / needing work.
        private static readonly HashSet<string> StubStatuses =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "stub", "draft", "todo", "wip", "empty", "incomplete" };

        /// <summary>Writes the HTML overview to a temp file and returns its path.</summary>
        public static string WriteHtmlOverviewToTempFile(
            IReadOnlyList<KbArticleIndex> index, string bankName)
        {
            var html = BuildHtmlOverview(index, bankName);
            var safe = MakeFileSafe(bankName);
            var path = Path.Combine(Path.GetTempPath(),
                $"supermemory-overview-{safe}-{DateTime.Now:yyyyMMdd-HHmmss}.html");
            File.WriteAllText(path, html, new UTF8Encoding(false));
            return path;
        }

        /// <summary>Builds the full self-contained HTML overview document.</summary>
        public static string BuildHtmlOverview(
            IReadOnlyList<KbArticleIndex> index, string bankName)
        {
            index = index ?? new List<KbArticleIndex>();

            var terms = index.Where(e => e.Folder == "02_TERMINOLOGY").ToList();
            var clients = index.Where(e => e.Folder == "01_CLIENTS").ToList();
            var domains = index.Where(e => e.Folder == "03_DOMAINS").ToList();
            var styles = index.Where(e => e.Folder == "04_STYLE").ToList();

            var conflicts = FindConflicts(terms);
            var stubs = index.Where(IsStub).ToList();
            var stale = index.Where(IsStale).ToList();
            var termsMissingDomain = terms.Where(t =>
                string.IsNullOrWhiteSpace(StripLinks(t.GetFrontmatter("domain")))).ToList();

            var sb = new StringBuilder(64 * 1024);
            sb.AppendLine("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
            sb.Append("<title>Memory Bank Overview – ").Append(Esc(bankName)).AppendLine("</title>");
            sb.AppendLine(Style());
            sb.AppendLine("</head><body>");

            // ── Header ──────────────────────────────────────────────
            sb.Append("<h1>Memory bank: ").Append(Esc(bankName)).AppendLine("</h1>");
            sb.Append("<p class=\"muted\">Generated ")
              .Append(Esc(DateTime.Now.ToString("yyyy-MM-dd HH:mm")))
              .Append(" • ").Append(index.Count).AppendLine(" notes</p>");

            // ── Dashboard cards ─────────────────────────────────────
            sb.AppendLine("<div class=\"cards\">");
            Card(sb, terms.Count, "Terminology");
            Card(sb, domains.Count, "Domains");
            Card(sb, clients.Count, "Clients");
            Card(sb, styles.Count, "Style guides");
            Card(sb, conflicts.Count, "Conflicts", conflicts.Count > 0 ? "warn" : null);
            Card(sb, stubs.Count, "Stubs", stubs.Count > 0 ? "warn" : null);
            Card(sb, stale.Count, "Stale (>1y)", stale.Count > 0 ? "warn" : null);
            sb.AppendLine("</div>");

            // ── Needs attention ─────────────────────────────────────
            if (conflicts.Count > 0 || stubs.Count > 0 || termsMissingDomain.Count > 0 || stale.Count > 0)
            {
                sb.AppendLine("<h2>Needs attention</h2>");

                if (conflicts.Count > 0)
                {
                    sb.AppendLine("<h3>Conflicting terminology <span class=\"muted\">(same source term, different targets)</span></h3>");
                    sb.AppendLine("<table class=\"grid\"><thead><tr><th>Source term</th><th>Conflicting targets</th><th>Notes</th></tr></thead><tbody>");
                    foreach (var c in conflicts)
                    {
                        sb.Append("<tr><td>").Append(Esc(c.SourceTerm)).Append("</td><td>")
                          .Append(Esc(string.Join("  |  ", c.Targets))).Append("</td><td class=\"muted\">")
                          .Append(Esc(string.Join("; ", c.Files))).AppendLine("</td></tr>");
                    }
                    sb.AppendLine("</tbody></table>");
                }

                if (stubs.Count > 0)
                    NoteList(sb, "Stubs / incomplete notes", stubs);
                if (termsMissingDomain.Count > 0)
                    NoteList(sb, "Terminology notes with no domain", termsMissingDomain);
                if (stale.Count > 0)
                    NoteList(sb, $"Not updated in over {StaleDays / 365} year(s)", stale.Take(50).ToList());
            }

            // ── Terminology table ───────────────────────────────────
            sb.AppendLine("<h2>Terminology</h2>");
            sb.AppendLine("<input id=\"q\" class=\"search\" type=\"search\" placeholder=\"Filter terms… (source, target, domain, client)\" oninput=\"filterRows()\">");
            sb.AppendLine("<table id=\"terms\" class=\"grid sortable\"><thead><tr>" +
                "<th onclick=\"sortBy(0)\">Source</th>" +
                "<th onclick=\"sortBy(1)\">Target</th>" +
                "<th onclick=\"sortBy(2)\">Domain</th>" +
                "<th onclick=\"sortBy(3)\">Client</th>" +
                "<th onclick=\"sortBy(4)\">Confidence</th>" +
                "<th onclick=\"sortBy(5)\">Status</th>" +
                "<th onclick=\"sortBy(6)\">Updated</th>" +
                "</tr></thead><tbody>");

            foreach (var t in terms
                .OrderBy(t => SourceTerm(t), StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("<tr><td>").Append(Esc(SourceTerm(t)))
                  .Append("</td><td>").Append(Esc(t.GetFrontmatter("term_target")))
                  .Append("</td><td>").Append(Esc(StripLinks(t.GetFrontmatter("domain"))))
                  .Append("</td><td>").Append(Esc(StripLinks(t.GetFrontmatter("client"))))
                  .Append("</td><td>").Append(Esc(t.GetFrontmatter("confidence")))
                  .Append("</td><td>").Append(Esc(t.GetFrontmatter("status")))
                  .Append("</td><td>").Append(Esc(GetDateString(t)))
                  .AppendLine("</td></tr>");
            }
            sb.AppendLine("</tbody></table>");

            // ── Domains & clients ───────────────────────────────────
            if (domains.Count > 0) NoteList(sb, "Domains", domains);
            if (clients.Count > 0) NoteList(sb, "Clients", clients);
            if (styles.Count > 0) NoteList(sb, "Style guides", styles);

            // ── Recent changes ──────────────────────────────────────
            var recent = index
                .Select(e => new { e, d = GetDate(e) })
                .Where(x => x.d.HasValue)
                .OrderByDescending(x => x.d.Value)
                .Take(20)
                .ToList();
            if (recent.Count > 0)
            {
                sb.AppendLine("<h2>Recently updated</h2>");
                sb.AppendLine("<table class=\"grid\"><thead><tr><th>Updated</th><th>Note</th><th>Folder</th></tr></thead><tbody>");
                foreach (var r in recent)
                {
                    sb.Append("<tr><td>").Append(Esc(r.d.Value.ToString("yyyy-MM-dd")))
                      .Append("</td><td>").Append(Esc(Title(r.e)))
                      .Append("</td><td class=\"muted\">").Append(Esc(r.e.Folder))
                      .AppendLine("</td></tr>");
                }
                sb.AppendLine("</tbody></table>");
            }

            sb.AppendLine(Script());
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        /// <summary>
        /// Builds a compact plain-text digest of the bank's metadata (counts,
        /// conflicts, stubs, and a capped source→target term list) for feeding to
        /// the LLM for a natural-language summary. Metadata only – no article
        /// bodies – so it stays small regardless of bank size.
        /// </summary>
        public static string BuildMetadataDigest(
            IReadOnlyList<KbArticleIndex> index, string bankName, int maxTerms = 400)
        {
            index = index ?? new List<KbArticleIndex>();
            var terms = index.Where(e => e.Folder == "02_TERMINOLOGY").ToList();
            var clients = index.Where(e => e.Folder == "01_CLIENTS").ToList();
            var domains = index.Where(e => e.Folder == "03_DOMAINS").ToList();
            var styles = index.Where(e => e.Folder == "04_STYLE").ToList();
            var conflicts = FindConflicts(terms);
            var stubs = index.Where(IsStub).ToList();
            var stale = index.Where(IsStale).ToList();

            var sb = new StringBuilder(16 * 1024);
            sb.Append("Memory bank: ").AppendLine(bankName);
            sb.Append("Totals: ").Append(index.Count).Append(" notes — ")
              .Append(terms.Count).Append(" terminology, ")
              .Append(domains.Count).Append(" domains, ")
              .Append(clients.Count).Append(" clients, ")
              .Append(styles.Count).AppendLine(" style guides.");
            sb.Append("Quality: ").Append(conflicts.Count).Append(" conflicting term pairs, ")
              .Append(stubs.Count).Append(" stubs, ")
              .Append(stale.Count).AppendLine(" stale (>1y).");

            // Domain coverage by counting terminology notes per domain tag.
            var domainCounts = terms
                .Select(t => StripLinks(t.GetFrontmatter("domain")))
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .GroupBy(d => d, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key} ({g.Count()})");
            sb.Append("Terminology by domain: ").AppendLine(string.Join(", ", domainCounts));

            if (conflicts.Count > 0)
            {
                sb.AppendLine().AppendLine("Conflicts (same source, different targets):");
                foreach (var c in conflicts.Take(40))
                    sb.Append("- ").Append(c.SourceTerm).Append(" -> ")
                      .AppendLine(string.Join(" | ", c.Targets));
            }

            sb.AppendLine().Append("Terminology entries (source -> target [domain]), up to ")
              .Append(maxTerms).AppendLine(":");
            foreach (var t in terms
                .OrderBy(t => SourceTerm(t), StringComparer.OrdinalIgnoreCase)
                .Take(maxTerms))
            {
                sb.Append("- ").Append(SourceTerm(t)).Append(" -> ")
                  .Append(t.GetFrontmatter("term_target") ?? "");
                var dom = StripLinks(t.GetFrontmatter("domain"));
                if (!string.IsNullOrWhiteSpace(dom)) sb.Append(" [").Append(dom).Append("]");
                sb.AppendLine();
            }
            if (terms.Count > maxTerms)
                sb.Append("… and ").Append(terms.Count - maxTerms).AppendLine(" more terminology notes.");

            return sb.ToString();
        }

        // ─── Analysis helpers ────────────────────────────────────────

        public class TermConflict
        {
            public string SourceTerm;
            public List<string> Targets = new List<string>();
            public List<string> Files = new List<string>();
        }

        private static List<TermConflict> FindConflicts(List<KbArticleIndex> terms)
        {
            var bySource = new Dictionary<string, TermConflict>(StringComparer.OrdinalIgnoreCase);

            foreach (var t in terms)
            {
                var src = SourceTerm(t);
                var tgt = (t.GetFrontmatter("term_target") ?? "").Trim();
                if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(tgt)) continue;

                if (!bySource.TryGetValue(src, out var c))
                {
                    c = new TermConflict { SourceTerm = src };
                    bySource[src] = c;
                }
                if (!c.Targets.Any(x => string.Equals(x, tgt, StringComparison.OrdinalIgnoreCase)))
                    c.Targets.Add(tgt);
                c.Files.Add(t.FileName);
            }

            return bySource.Values
                .Where(c => c.Targets.Count > 1)
                .OrderBy(c => c.SourceTerm, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsStub(KbArticleIndex e)
        {
            var status = (e.GetFrontmatter("status") ?? "").Trim();
            if (StubStatuses.Contains(status)) return true;
            // Very small files are almost certainly placeholder stubs (the
            // frontmatter alone is usually a few hundred bytes).
            return e.FileSizeBytes > 0 && e.FileSizeBytes < 300;
        }

        private static bool IsStale(KbArticleIndex e)
        {
            var d = GetDate(e);
            return d.HasValue && (DateTime.Now - d.Value).TotalDays > StaleDays;
        }

        private static DateTime? GetDate(KbArticleIndex e)
        {
            foreach (var key in new[] { "updated", "modified", "created", "date" })
            {
                var raw = e.GetFrontmatter(key);
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (DateTime.TryParse(raw.Trim(), CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var d))
                    return d;
            }
            return null;
        }

        private static string GetDateString(KbArticleIndex e)
        {
            var d = GetDate(e);
            return d.HasValue ? d.Value.ToString("yyyy-MM-dd") : "";
        }

        private static string SourceTerm(KbArticleIndex e)
        {
            var s = e.GetFrontmatter("term_source");
            if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            return Title(e);
        }

        private static string Title(KbArticleIndex e)
        {
            var t = e.GetFrontmatter("title");
            if (!string.IsNullOrWhiteSpace(t)) return t.Trim();
            return Path.GetFileNameWithoutExtension(e.FileName);
        }

        // ─── HTML helpers ────────────────────────────────────────────

        private static void Card(StringBuilder sb, int n, string label, string cls = null)
        {
            sb.Append("<div class=\"card")
              .Append(cls != null ? " " + cls : "")
              .Append("\"><div class=\"n\">").Append(n)
              .Append("</div><div class=\"l\">").Append(Esc(label)).AppendLine("</div></div>");
        }

        private static void NoteList(StringBuilder sb, string heading, List<KbArticleIndex> notes)
        {
            sb.Append("<h3>").Append(Esc(heading)).Append(" <span class=\"muted\">(")
              .Append(notes.Count).AppendLine(")</span></h3><ul class=\"notes\">");
            foreach (var n in notes.OrderBy(x => Title(x), StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("<li>").Append(Esc(Title(n)))
                  .Append(" <span class=\"muted\">").Append(Esc(n.RelativePath))
                  .AppendLine("</span></li>");
            }
            sb.AppendLine("</ul>");
        }

        private static string StripLinks(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Replace("[[", "").Replace("]]", "").Trim();
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;");
        }

        private static string MakeFileSafe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "bank";
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '-');
            return s;
        }

        private static string Style() => @"<style>
:root{--fg:#1c2330;--muted:#6b7480;--line:#e2e6ec;--accent:#1e5a9e;--warn:#b54708;--warnbg:#fff4e6;}
*{box-sizing:border-box}
body{font:14px/1.5 'Segoe UI',system-ui,sans-serif;color:var(--fg);margin:0;padding:24px 32px;max-width:1200px}
h1{font-size:22px;margin:0 0 2px}
h2{font-size:17px;margin:28px 0 10px;border-bottom:2px solid var(--line);padding-bottom:4px}
h3{font-size:14px;margin:18px 0 6px}
.muted{color:var(--muted);font-weight:400}
.cards{display:flex;flex-wrap:wrap;gap:12px;margin:16px 0}
.card{border:1px solid var(--line);border-radius:8px;padding:12px 16px;min-width:110px}
.card .n{font-size:24px;font-weight:600;color:var(--accent)}
.card .l{font-size:12px;color:var(--muted)}
.card.warn{background:var(--warnbg);border-color:#f0c089}
.card.warn .n{color:var(--warn)}
table.grid{border-collapse:collapse;width:100%;margin:6px 0 4px;font-size:13px}
table.grid th,table.grid td{border:1px solid var(--line);padding:5px 9px;text-align:left;vertical-align:top}
table.grid thead th{background:#f5f7fa;position:sticky;top:0}
table.sortable thead th{cursor:pointer;user-select:none}
table.grid tbody tr:nth-child(even){background:#fafbfc}
.search{width:100%;max-width:480px;padding:7px 10px;border:1px solid var(--line);border-radius:6px;margin:4px 0 8px;font-size:13px}
ul.notes{margin:4px 0 8px;padding-left:20px;columns:2;font-size:13px}
ul.notes li{margin:1px 0}
</style>";

        private static string Script() => @"<script>
function filterRows(){
  var q=document.getElementById('q').value.toLowerCase();
  var rows=document.querySelectorAll('#terms tbody tr');
  rows.forEach(function(r){r.style.display=r.textContent.toLowerCase().indexOf(q)>=0?'':'none';});
}
var sortState={};
function sortBy(col){
  var tb=document.querySelector('#terms tbody');
  var rows=Array.prototype.slice.call(tb.querySelectorAll('tr'));
  var asc=sortState[col]=!sortState[col];
  rows.sort(function(a,b){
    var x=a.cells[col].textContent.trim().toLowerCase();
    var y=b.cells[col].textContent.trim().toLowerCase();
    if(x<y)return asc?-1:1; if(x>y)return asc?1:-1; return 0;
  });
  rows.forEach(function(r){tb.appendChild(r);});
}
</script>";
    }
}

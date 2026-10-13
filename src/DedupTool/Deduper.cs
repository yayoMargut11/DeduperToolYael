using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DedupTool;

/// <summary>
/// Suppresses duplicate content in a batch while keeping families intact.
/// </summary>
public static class Deduper
{
    public static DedupResult Run(Batch batch)
    {
        var docs = batch.Documents;

        var byId = new Dictionary<string, Document>(docs.Count, StringComparer.Ordinal);
        foreach (var d in docs)
        {
            if (!byId.TryAdd(d.DocId, d))
                throw new InvalidDataException($"docId '{d.DocId}' appears more than once in the batch.");
        }

        // 1. Group every document into the family of its top-level ancestor.
        var rootOf = new Dictionary<string, string>(docs.Count, StringComparer.Ordinal);
        var families = new Dictionary<string, Family>(StringComparer.Ordinal);
        foreach (var d in docs)
        {
            var rootId = ResolveRoot(d, byId, rootOf);
            if (!families.TryGetValue(rootId, out var fam))
            {
                fam = new Family(byId[rootId]);
                families[rootId] = fam;
            }
            fam.Members.Add(d);
        }

        // 2. Bucket families that claim to be the same content.
        var groups = new Dictionary<string, List<Family>>(StringComparer.Ordinal);
        foreach (var fam in families.Values)
        {
            fam.AttachmentHashes = new HashSet<string>(
                fam.Members.Where(m => !ReferenceEquals(m, fam.Root)).Select(m => Norm(m.Sha256)),
                StringComparer.Ordinal);

            var key = DedupKey(fam.Root);
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<Family>();
                groups[key] = list;
            }
            list.Add(fam);
        }

        // 3. Within each bucket keep the most complete family first; a family is only
        //    suppressed if every attachment it has is also present in a family we keep.
        var rows = new List<LoadFileRow>(docs.Count);
        var suppressed = 0;
        foreach (var list in groups.Values)
        {
            list.Sort((a, b) =>
            {
                var bySize = b.AttachmentHashes.Count.CompareTo(a.AttachmentHashes.Count);
                return bySize != 0 ? bySize : string.CompareOrdinal(a.Root.DocId, b.Root.DocId);
            });

            var kept = new List<Family>(1);
            foreach (var fam in list)
            {
                if (kept.Any(k => fam.AttachmentHashes.IsSubsetOf(k.AttachmentHashes)))
                {
                    suppressed += fam.Members.Count;
                    continue;
                }
                kept.Add(fam);
                EmitFamily(fam, rows);
            }
        }

        rows.Sort((a, b) => string.CompareOrdinal(a.DocId, b.DocId));
        return new DedupResult(rows, suppressed);
    }

    private static void EmitFamily(Family fam, List<LoadFileRow> rows)
    {
        // Spec section 5: attachments carry the family's top-level email date; loose files have none.
        var sentDate = IsType(fam.Root, "email") ? FormatUtc(fam.Root) : "";

        foreach (var d in fam.Members)
        {
            rows.Add(new LoadFileRow
            {
                DocId = d.DocId,
                FamilyId = fam.Root.DocId,
                ParentId = d.ParentId ?? "",
                Custodian = d.Custodian,
                DocType = d.DocType,
                FileName = d.FileName,
                SentDate = IsType(d, "loose") ? "" : sentDate,
                Hash = d.Sha256,
            });
        }
    }

    // ---- family resolution ------------------------------------------------

    private static string ResolveRoot(Document start, Dictionary<string, Document> byId, Dictionary<string, string> memo)
    {
        var path = new List<string>();
        var onPath = new HashSet<string>(StringComparer.Ordinal);
        var cur = start;
        string root;

        while (true)
        {
            if (memo.TryGetValue(cur.DocId, out var known)) { root = known; break; }
            if (!onPath.Add(cur.DocId))
                throw new InvalidDataException($"parentId cycle detected at docId '{cur.DocId}'.");
            path.Add(cur.DocId);

            if (string.IsNullOrEmpty(cur.ParentId)) { root = cur.DocId; break; }
            if (!byId.TryGetValue(cur.ParentId, out var parent))
                throw new InvalidDataException(
                    $"docId '{cur.DocId}' has parentId '{cur.ParentId}', which is not in the batch.");
            cur = parent;
        }

        foreach (var id in path) memo[id] = root;
        return root;
    }

    // ---- duplicate keys ---------------------------------------------------

    private static string DedupKey(Document root)
    {
        if (IsType(root, "email"))
            return "E:" + EmailKey(root);

        // loose files (3.1), or any other top-level document: content hash within its own type.
        return root.DocType.ToLowerInvariant() + ":" + Norm(root.Sha256);
    }

    /// <summary>
    /// Identity of an email independent of the mailbox it was collected from (3.3).
    /// Built from: sent instant (UTC, to the second), sender, recipient set, subject, body.
    /// Deliberately excludes sha256, file name, custodian and size, which vary per mailbox.
    /// </summary>
    private static string EmailKey(Document e)
    {
        var sb = new StringBuilder();
        sb.Append(SentInstantTicks(e)).Append('\u001f');
        sb.Append(NormAddress(e.From)).Append('\u001f');

        var recipients = (e.To ?? new List<string>())
            .Select(NormAddress)
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(a => a, StringComparer.Ordinal);
        sb.Append(string.Join("\u001e", recipients)).Append('\u001f');

        sb.Append(NormSubject(e.Subject)).Append('\u001f');
        sb.Append(NormBody(e.Body));

        // Hash the key so we don't keep two million full bodies alive as dictionary keys.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string NormAddress(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        var lt = s.LastIndexOf('<');
        var gt = s.LastIndexOf('>');
        if (lt >= 0 && gt > lt) s = s.Substring(lt + 1, gt - lt - 1);   // "John Smith <j@x.com>" -> "j@x.com"
        return s.Trim().ToLowerInvariant();
    }

    private static string NormSubject(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // Folded headers can change whitespace; collapse it. Case and "Re:"/"FW:" prefixes are significant.
        return string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormBody(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var lines = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return string.Join('\n', lines.Select(l => l.TrimEnd())).Trim();
    }

    // ---- dates ------------------------------------------------------------

    private static bool TryParseDate(string? s, out DateTimeOffset dto) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dto);

    private static long SentInstantTicks(Document e)
    {
        if (string.IsNullOrWhiteSpace(e.SentDate)) return -1;
        if (!TryParseDate(e.SentDate, out var dto))
            throw new InvalidDataException($"docId '{e.DocId}' has an unparseable sentDate '{e.SentDate}'.");
        return dto.UtcDateTime.Ticks / TimeSpan.TicksPerSecond;
    }

    /// <summary>YYYY-MM-DDTHH:MM:SSZ, UTC, no fractional seconds.</summary>
    public static string FormatUtc(Document top)
    {
        if (string.IsNullOrWhiteSpace(top.SentDate)) return "";
        if (!TryParseDate(top.SentDate, out var dto))
            throw new InvalidDataException($"docId '{top.DocId}' has an unparseable sentDate '{top.SentDate}'.");
        return dto.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    // ---- helpers ----------------------------------------------------------

    private static bool IsType(Document d, string type) =>
        string.Equals(d.DocType, type, StringComparison.OrdinalIgnoreCase);

    private static string Norm(string hash) => (hash ?? "").Trim().ToLowerInvariant();

    private sealed class Family
    {
        public Family(Document root) { Root = root; }
        public Document Root { get; }
        public List<Document> Members { get; } = new();
        public HashSet<string> AttachmentHashes { get; set; } = new();
    }
}

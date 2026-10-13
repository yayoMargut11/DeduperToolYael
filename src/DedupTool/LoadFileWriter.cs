using System.Text;

namespace DedupTool;

/// <summary>
/// Writes the load file consumed by the review platform ingest job.
/// Column order is fixed. Do not change without coordinating with the ingest team.
/// </summary>
public static class LoadFileWriter
{
    public const string Header = "DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash";

    public static string ToCsv(IEnumerable<LoadFileRow> rows)
    {
        using var sw = new StringWriter { NewLine = "\n" };
        WriteTo(sw, rows);
        return sw.ToString();
    }

    public static void Write(string path, IEnumerable<LoadFileRow> rows)
    {
        // UTF-8 without BOM, LF line endings, streamed so a 2M-row file is never built in memory.
        using var sw = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" };
        WriteTo(sw, rows);
    }

    private static void WriteTo(TextWriter w, IEnumerable<LoadFileRow> rows)
    {
        // Ingest job expects LF line endings regardless of platform.
        w.Write(Header);
        w.Write('\n');
        foreach (var r in rows)
        {
            w.Write(Field(r.DocId)); w.Write(',');
            w.Write(Field(r.FamilyId)); w.Write(',');
            w.Write(Field(r.ParentId)); w.Write(',');
            w.Write(Field(r.Custodian)); w.Write(',');
            w.Write(Field(r.DocType)); w.Write(',');
            w.Write(Field(r.FileName)); w.Write(',');
            w.Write(Field(r.SentDate)); w.Write(',');
            w.Write(Field(r.Hash));
            w.Write('\n');
        }
    }

    /// <summary>RFC 4180 quoting: file names can legitimately contain commas, quotes or newlines.</summary>
    internal static string Field(string? value)
    {
        value ??= "";
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}

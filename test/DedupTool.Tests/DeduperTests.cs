using DedupTool;
using Xunit;

namespace DedupTool.Tests;

public class DeduperTests
{
    // ---- builders ---------------------------------------------------------

    private static Document Loose(string id, string custodian, string hash) => new()
    {
        DocId = id, Custodian = custodian, DocType = "loose", FileName = id + ".txt", Sha256 = hash, SizeBytes = 10,
    };

    private static Document Email(string id, string custodian, string hash, string sentDate = "2024-03-05T14:22:00-05:00",
        string from = "john@example.com", string[]? to = null, string subject = "Hello", string body = "Body text") => new()
    {
        DocId = id, Custodian = custodian, DocType = "email", FileName = id + ".eml", Sha256 = hash, SizeBytes = 100,
        SentDate = sentDate, From = from, To = (to ?? new[] { "maria@example.com" }).ToList(), Subject = subject, Body = body,
    };

    private static Document Attach(string id, string custodian, string parent, string hash) => new()
    {
        DocId = id, Custodian = custodian, DocType = "attachment", ParentId = parent, FileName = id + ".bin", Sha256 = hash, SizeBytes = 50,
    };

    private static Batch B(params Document[] docs) => new() { BatchId = "t", Documents = docs.ToList() };

    private static string[] Ids(DedupResult r) => r.Rows.Select(x => x.DocId).ToArray();

    // ---- original behaviour (kept) ---------------------------------------

    [Fact]
    public void IdenticalLooseDocuments_SecondIsSuppressed()
    {
        var result = Deduper.Run(B(Loose("A", "c1", "h1"), Loose("B", "c2", "h1")));

        Assert.Single(result.Rows);
        Assert.Equal("A", result.Rows[0].DocId);
        Assert.Equal(1, result.Suppressed);
    }

    [Fact]
    public void DistinctDocuments_AllKept()
    {
        var result = Deduper.Run(B(Loose("A", "c1", "h1"), Loose("B", "c1", "h2")));

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(0, result.Suppressed);
    }

    [Fact]
    public void LoadFile_StartsWithFixedHeader()
    {
        var csv = LoadFileWriter.ToCsv(Array.Empty<LoadFileRow>());

        Assert.StartsWith("DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash", csv);
    }

    // ---- INGEST-4471: families stay intact --------------------------------

    [Fact]
    public void SameAttachmentInTwoDifferentEmails_IsKeptInBothFamilies()
    {
        // The bug: the 2nd copy of vendor-report.pdf was suppressed, orphaning the forward from its attachment.
        var result = Deduper.Run(B(
            Email("E1", "c1", "e1", subject: "Original"),
            Attach("A1", "c1", "E1", "pdf"),
            Email("E2", "c1", "e2", subject: "FW: Original"),
            Attach("A2", "c1", "E2", "pdf")));

        Assert.Equal(new[] { "A1", "A2", "E1", "E2" }, Ids(result));
        Assert.Equal(0, result.Suppressed);
        Assert.Equal("E2", result.Rows.Single(r => r.DocId == "A2").FamilyId);
    }

    // ---- 3.3 cross-mailbox email deduplication ----------------------------

    [Fact]
    public void SameEmailFromTwoMailboxes_IsSuppressedWithItsWholeFamily()
    {
        var sent = Email("E1", "jsmith", "hash-a", sentDate: "2024-03-05T14:22:00-05:00",
            from: "john.smith@example.com", subject: "Q3  budget", body: "Hi\nThanks,\nJohn");
        var inbox = Email("E2", "mlee", "hash-b", sentDate: "2024-03-05T19:22:00Z",
            from: "John.Smith@Example.com", subject: "Q3 budget", body: "Hi\r\nThanks, \r\nJohn\r\n");

        var result = Deduper.Run(B(
            sent, Attach("A1", "jsmith", "E1", "x"), Attach("A2", "jsmith", "E1", "y"),
            inbox, Attach("A3", "mlee", "E2", "x"), Attach("A4", "mlee", "E2", "y")));

        Assert.Equal(new[] { "A1", "A2", "E1" }, Ids(result));
        Assert.Equal(3, result.Suppressed);
    }


    // ---- family integrity and deterministic output -----------------------

    [Fact]
    public void NestedAttachments_ResolveToTopLevelEmailFamily()
    {
        var result = Deduper.Run(B(
            Attach("A2", "c1", "A1", "nested"),
            Email("E1", "c1", "email-hash"),
            Attach("A1", "c1", "E1", "outer")));

        Assert.Equal(new[] { "A1", "A2", "E1" }, Ids(result));
        Assert.All(result.Rows, row => Assert.Equal("E1", row.FamilyId));
        Assert.Equal("A1", result.Rows.Single(r => r.DocId == "A2").ParentId);
    }

    [Fact]
    public void Output_IsSortedByDocIdRegardlessOfInputOrder()
    {
        var first = Deduper.Run(B(
            Loose("Z", "c1", "hash-z"), Loose("A", "c1", "hash-a"),
            Loose("M", "c1", "hash-m")));
        var second = Deduper.Run(B(
            Loose("M", "c1", "hash-m"), Loose("Z", "c1", "hash-z"),
            Loose("A", "c1", "hash-a")));

        Assert.Equal(new[] { "A", "M", "Z" }, Ids(first));
        Assert.Equal(Ids(first), Ids(second));
    }

    [Fact]
    public void DuplicateDocIds_ThrowInvalidDataException()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            Deduper.Run(B(Loose("A", "c1", "h1"), Loose("A", "c2", "h2"))));

        Assert.Contains("docId 'A' appears more than once", ex.Message);
    }

    [Fact]
    public void MissingParent_ThrowsInvalidDataException()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            Deduper.Run(B(Attach("A1", "c1", "MISSING", "h1"))));

        Assert.Contains("parentId 'MISSING'", ex.Message);
    }

    [Fact]
    public void ParentCycle_ThrowsInvalidDataException()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            Deduper.Run(B(
                Attach("A1", "c1", "A2", "h1"),
                Attach("A2", "c1", "A1", "h2"))));

        Assert.Contains("parentId cycle detected", ex.Message);
    }

    // ---- family deduplication policy -------------------------------------

    [Fact]
    public void DuplicateEmailFamilyWithSubsetOfAttachments_IsSuppressedAsAWhole()
    {
        var result = Deduper.Run(B(
            Email("E2", "c2", "email-bytes-2"),
            Attach("A2x", "c2", "E2", "x"),
            Email("E1", "c1", "email-bytes-1"),
            Attach("A1x", "c1", "E1", "x"),
            Attach("A1y", "c1", "E1", "y")));

        Assert.Equal(new[] { "A1x", "A1y", "E1" }, Ids(result));
        Assert.Equal(2, result.Suppressed);
        Assert.DoesNotContain(result.Rows, r => r.DocId == "E2" || r.DocId == "A2x");
    }

    [Fact]
    public void DuplicateEmailFamiliesWithSameAttachmentCount_KeepLexicographicallyFirstRoot()
    {
        var result = Deduper.Run(B(
            Email("E2", "c2", "bytes-2"),
            Attach("A2", "c2", "E2", "x"),
            Email("E1", "c1", "bytes-1"),
            Attach("A1", "c1", "E1", "x")));

        Assert.Equal(new[] { "A1", "E1" }, Ids(result));
        Assert.Equal(2, result.Suppressed);
    }

    [Fact]
    public void SameHashAcrossDifferentTopLevelTypes_IsNotDeduplicated()
    {
        var result = Deduper.Run(B(
            Loose("L1", "c1", "same-hash"),
            new Document
            {
                DocId = "A1", Custodian = "c1", DocType = "attachment",
                ParentId = null, FileName = "a.bin", Sha256 = "same-hash",
            }));

        Assert.Equal(new[] { "A1", "L1" }, Ids(result));
        Assert.Equal(0, result.Suppressed);
    }

    // ---- email identity and normalization --------------------------------

    [Fact]
    public void EmailRecipientOrderDuplicatesAndDisplayNames_DoNotAffectIdentity()
    {
        var one = Email("E1", "c1", "raw-1",
            from: "John Smith <JOHN@example.com>",
            to: new[] { "Mary <mary@example.com>", "other@example.com", "mary@example.com" },
            subject: "Quarterly report", body: "Content");
        var two = Email("E2", "c2", "raw-2",
            from: "john@EXAMPLE.com",
            to: new[] { "OTHER@example.com", "MARY@example.com" },
            subject: "Quarterly report", body: "Content");

        var result = Deduper.Run(B(one, two));

        Assert.Equal(new[] { "E1" }, Ids(result));
        Assert.Equal(1, result.Suppressed);
    }

    [Fact]
    public void DifferentEmailBody_MeansEmailsAreNotDuplicates()
    {
        var result = Deduper.Run(B(
            Email("E1", "c1", "same-raw-hash", body: "First body"),
            Email("E2", "c2", "same-raw-hash", body: "Different body")));

        Assert.Equal(new[] { "E1", "E2" }, Ids(result));
        Assert.Equal(0, result.Suppressed);
    }

    [Fact]
    public void DifferentEmailSubject_MeansEmailsAreNotDuplicates()
    {
        var result = Deduper.Run(B(
            Email("E1", "c1", "h1", subject: "Original"),
            Email("E2", "c2", "h2", subject: "Re: Original")));

        Assert.Equal(new[] { "E1", "E2" }, Ids(result));
        Assert.Equal(0, result.Suppressed);
    }

    [Fact]
    public void DifferentEmailRecipients_MeansEmailsAreNotDuplicates()
    {
        var result = Deduper.Run(B(
            Email("E1", "c1", "h1", to: new[] { "a@example.com" }),
            Email("E2", "c2", "h2", to: new[] { "b@example.com" })));

        Assert.Equal(new[] { "E1", "E2" }, Ids(result));
        Assert.Equal(0, result.Suppressed);
    }

    [Fact]
    public void DifferentSentInstants_MeanEmailsAreNotDuplicates()
    {
        var result = Deduper.Run(B(
            Email("E1", "c1", "h1", sentDate: "2024-03-05T19:22:00Z"),
            Email("E2", "c2", "h2", sentDate: "2024-03-05T19:22:01Z")));

        Assert.Equal(new[] { "E1", "E2" }, Ids(result));
        Assert.Equal(0, result.Suppressed);
    }

    [Fact]
    public void InvalidEmailSentDate_ThrowsInvalidDataException()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            Deduper.Run(B(Email("E1", "c1", "h1", sentDate: "not-a-date"))));

        Assert.Contains("unparseable sentDate", ex.Message);
    }

    // ---- load-file values produced by Deduper -----------------------------

    [Fact]
    public void EmailAndAttachments_UseUtcDateWithoutFractionalSeconds()
    {
        var result = Deduper.Run(B(
            Email("E1", "c1", "h1", sentDate: "2024-03-05T14:22:00.987-05:00"),
            Attach("A1", "c1", "E1", "attachment-hash"),
            Loose("L1", "c1", "loose-hash")));

        Assert.Equal("2024-03-05T19:22:00Z", result.Rows.Single(r => r.DocId == "E1").SentDate);
        Assert.Equal("2024-03-05T19:22:00Z", result.Rows.Single(r => r.DocId == "A1").SentDate);
        Assert.Equal("", result.Rows.Single(r => r.DocId == "L1").SentDate);
        Assert.Equal("L1", result.Rows.Single(r => r.DocId == "L1").FamilyId);
    }

    [Fact]
    public void FormatUtc_EmptyDateReturnsEmptyString()
    {
        Assert.Equal("", Deduper.FormatUtc(Email("E1", "c1", "h1", sentDate: "")));
    }

}

using System.Text.Json;
using DedupTool;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: DedupTool <batch.json> <loadfile.csv>");
            return 2;
        }

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            Batch batch;
            using (var input = File.OpenRead(args[0]))   // stream: batches can be very large
            {
                batch = JsonSerializer.Deserialize<Batch>(input, options)
                        ?? throw new InvalidDataException("Batch file was empty.");
            }

            var result = Deduper.Run(batch);
            LoadFileWriter.Write(args[1], result.Rows);

            Console.WriteLine($"{batch.BatchId}: {batch.Documents.Count} documents in, {result.Rows.Count} out, {result.Suppressed} suppressed as duplicates.");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
        {
            // Fail loudly and write nothing rather than produce a load file that silently breaks a family.
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }
}

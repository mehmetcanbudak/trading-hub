using TradingHub.Data;

namespace TradingHub.App.Services;

/// <summary>Runs before singleton, Avalonia and credential-store initialization.</summary>
public static class IbImportCommand
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            int index = Array.IndexOf(args, "--import-ib");
            if (index < 0 || index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Usage: --import-ib <folder> [symbol] [--replace] [--preview]");
            string folder = args[index + 1];
            string? symbol = index + 2 < args.Length && !args[index + 2].StartsWith("--", StringComparison.Ordinal) ? args[index + 2] : null;
            var accepted = new HashSet<string> { "--replace", "--preview", "--import-ib" };
            for (int i = 0; i < args.Length; i++)
                if (i != index + 1 && !(symbol != null && i == index + 2) && !accepted.Contains(args[i])) throw new ArgumentException($"Unknown IB import argument: {args[i]}");
            var preview = IbBarImport.PreviewFolder(folder, AppPaths.Ticks, symbol);
            output.WriteLine(preview.Description);
            foreach (var day in preview.Days) output.WriteLine($"{day.Day:yyyy-MM-dd}: {day.Bars} incoming bars, {day.StoredSyntheticMinutes} stored synthetic minutes, {day.RecordedTrades} recorded trades, {day.CoveredRealMinutes} overlapping real minutes");
            if (args.Contains("--preview")) return 0;
            bool replace = args.Contains("--replace");
            if (preview.Conflicts > 0 && !replace)
                output.WriteLine($"Preserving {preview.Conflicts} recorded minute(s) byte-for-byte; only uncovered minutes are imported. Destructive replacement is refused unless --replace is explicitly supplied.");
            var result = IbBarImport.Commit(preview, AppPaths.Ticks, replace ? IbBarImport.ImportPolicy.ReplaceCoveredMinutes : IbBarImport.ImportPolicy.PreserveRecorded);
            output.WriteLine($"Published {result.FilesWritten} file(s). Policy: {result.Policy}. Preserved backups and transaction journal: {result.BackupDirectory}");
            return 0;
        }
        catch (Exception ex)
        {
            error.WriteLine("IB import failed: " + ex.Message);
            return 2;
        }
    }
}

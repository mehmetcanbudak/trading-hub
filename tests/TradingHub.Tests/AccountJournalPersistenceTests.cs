using TradingHub.Trading;

namespace TradingHub.Tests;

public sealed class AccountJournalPersistenceTests
{
    [Fact]
    public void A_failed_write_does_not_poison_execution_deduplication_after_storage_repair()
    {
        string root = Path.Combine(Path.GetTempPath(), "TradingHub-account-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string account = "isolated-account";
            var journal = new AccountJournal(root);
            journal.FillsOf(account); // Load the empty history before injecting the append-open failure.
            var time = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            var entry = new AccountFill("entry-execution", time, "TEST", true, 2m, 100m, 10m, 3m, "entry-order");
            var exit = new AccountFill("exit-execution", time.AddMinutes(1), "TEST", false, 2m, 105m, 10m, 3m, "exit-order");
            string path = Path.Combine(root, account + ".jsonl");
            using (var blocked = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                Assert.Throws<IOException>(() => journal.Add(account, entry));

            Assert.True(journal.Add(account, entry));
            Assert.True(journal.Add(account, exit));
            var recovered = new AccountJournal(root);
            Assert.Equal(new[] { entry, exit }, recovered.FillsOf(account));
            // Independent round-trip economics survive repair and reload: 100 gross - 6 execution charges = 94 net.
            Assert.Equal(94m, RoundTrips.Analyze(recovered.FillsOf(account)).RealizedNet);
            Assert.False(recovered.Add(account, entry));
            Assert.Equal(new[] { entry, exit }, recovered.FillsOf(account));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_retry_after_a_torn_tail_is_still_present_after_reload()
    {
        string root = Path.Combine(Path.GetTempPath(), "TradingHub-account-torn-tail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string account = "isolated-account";
            var journal = new AccountJournal(root);
            journal.FillsOf(account);
            File.WriteAllText(Path.Combine(root, account + ".jsonl"), "{\"Id\":\"interrupted-entry");
            var time = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            var entry = new AccountFill("entry", time, "TEST", true, 2m, 100m, 10m, 3m);
            var exit = new AccountFill("exit", time.AddMinutes(1), "TEST", false, 2m, 105m, 10m, 3m);
            Assert.True(journal.Add(account, entry));
            Assert.True(journal.Add(account, exit));
            var recovered = new AccountJournal(root);
            Assert.Equal(94m, RoundTrips.Analyze(recovered.FillsOf(account)).RealizedNet);
            Assert.Equal(new[] { entry, exit }, recovered.FillsOf(account));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_restart_namespace_failure_does_not_keep_archived_execution_ids_in_the_active_history()
    {
        string root = Path.Combine(Path.GetTempPath(), "TradingHub-account-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string account = "isolated-account";
            var journal = new AccountJournal(root);
            var time = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            journal.Add(account, new AccountFill("entry", time, "TEST", true, 2m, 100m, 10m, 3m));
            journal.Add(account, new AccountFill("exit", time.AddMinutes(1), "TEST", false, 2m, 105m, 10m, 3m));
            Assert.Throws<IOException>(() => journal.Restart(account, _ => throw new IOException("Owned post-rename namespace failure.")));
            Assert.Equal(0m, RoundTrips.Analyze(journal.FillsOf(account)).RealizedNet);
            var entry = new AccountFill("entry", time.AddHours(1), "TEST", true, 1m, 200m, 10m, 1m);
            var exit = new AccountFill("exit", time.AddHours(1).AddMinutes(1), "TEST", false, 1m, 210m, 10m, 1m);
            Assert.True(journal.Add(account, entry));
            Assert.True(journal.Add(account, exit));
            var recovered = new AccountJournal(root);
            Assert.Equal(98m, RoundTrips.Analyze(recovered.FillsOf(account)).RealizedNet);
            Assert.Equal(new[] { entry, exit }, recovered.FillsOf(account));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

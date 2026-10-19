[← Documentation index](README.md)

# Keep the English fork aligned with upstream

This fork tracks `ralphtradesz/trading-hub:main` while maintaining English work on `mehmetcanbudak/trading-hub:feat/english-localization`. Rust/Swift ports remain separate worktrees/branches. **The monitor never pushes to the original author's repository, merges code, translates automatically, changes your scripts/data or installs the app.**

## What is tracked

- [Upstream ledger](../../.github/upstream-english.json): the exact upstream commit already incorporated **and reviewed in English**. `integrated_commit` is not the English branch's own commit hash.
- [Scheduled monitor](../../.github/workflows/upstream-english-tracking.yml): checks daily at **06:37 UTC**, on relevant tracker/checkpoint pushes, or manually through Actions → Track upstream for English localization → Run workflow.
- One **[English] Upstream synchronization tracker** issue in this fork: current checkpoint/tip, pending commits, affected source/docs, critical trading/data/API paths and a human review checklist. The first run assigns the issue to the fork owner.
- New upstream tips needing review get one deduplicated comment mentioning the owner. Repeated checks of an unchanged tip do not send another comment. Your GitHub notification settings determine email/push delivery.
- The bot replaces only its marked report section. Keep notes and checklist progress outside that section; they are preserved. Recheck completed items when another upstream tip arrives. A closed tracker reopens while unreviewed work remains.
- [Translation status](TRANSLATION_STATUS.md) records actual build, compatibility and UI evidence. [English validation](../../.github/workflows/english-translation-validation.yml) runs for pull requests into the English branch as well as pushes.

The fork's default branch is English so GitHub can run its schedule there; `main` code is not changed for this setup. The monitor's token has read-only contents and issue-write permissions, not code-write or merge permissions. It fetches upstream Git objects but never checks out or executes upstream code.

## Check now without writing GitHub issues

From the English worktree:

```bash
python3 tools/check_upstream.py
```

This fetches into the dedicated remote-tracking ref `refs/remotes/english-tracker/upstream` and prints the review queue. It does not switch branches, merge, update the ledger or alter tracked files. It refuses a main/port checkout. Git and Python 3 are required; GitHub CLI is needed only for notifications.

If upstream rewrites history or the ledger is invalid, investigate it. **Do not automatically reset the checkpoint or force-push this fork.** Large queues are summarized with a link to the complete upstream comparison; the lists are review candidates, not exhaustive language classification.

## Incorporate an update safely

1. Read the tracker and upstream commits. Decide what changed in behavior, storage, brokerage safety, dependency/license requirements and user-facing language. Translation is not permission to alter account/order IDs or blindly rename existing user code.
2. Start from a clean English worktree. If another task uses it, create a separate worktree rather than switching its branch or stashing its files. Fetch and create a dedicated sync branch:
   ```bash
   git switch feat/english-localization
   git pull --ff-only origin feat/english-localization
   git fetch upstream main
   git switch -c sync/upstream-SHORT_SHA
   git merge --no-ff --no-commit FULL_UPSTREAM_SHA
   ```
   Use the exact reviewed SHA from the tracker, not an unreviewed moving tip. Resolve conflicts manually. Abort with `git merge --abort` if the integration is not ready; never use a hard reset to discard unknown work.
3. Review every changed surface:
   - App labels, safety dialogs, tooltips, error/status messages, charts/DOM/footprint, reports and assistant instructions.
   - SDK/localization defaults, native indicators, **all** sample/new-code generators and English code identifiers for new samples. Keep generated SDK resources and existing custom IDs compatible.
   - Full English guides and their examples/images. A new `docs/feature.md` needs `docs/en/feature.md`; upstream README/CLAUDE changes must be reconciled with both retained Spanish originals and English instructions.
   - Core trading/data/protocol changes separately from wording. Preserve credentials, formats, persisted keys, recovery rules and order safeguards.
4. Run the existing gates and inspect new/changed UI flows with **fresh isolated fixtures**, fake brokers/credentials and disabled sleep integration:
   ```bash
   export PATH="$HOME/.dotnet:$PATH"
   dotnet build TradingHub.slnx -c Release
   dotnet test tests/TradingHub.Tests -c Release
   python3 -m unittest discover -s tools/tests -p 'test_*.py' -v
   python3 tools/check_english_translation.py
   git diff --check
   ```
   Use the full UI commands in [Translation status](TRANSLATION_STATUS.md#reproduce). Review actual safety-dialog cancellation, English default generators and relevant reports. Do not use real orders, paid data/Claude calls, privileged power changes or personal data as fixtures.
5. New source-structure audit failures require review, not bypassing the check. Update only individually reviewed entries in `tools/english-localization-review.json`, then rerun all gates. The historical audit baseline in that file and `EnglishLocalizationAudit.cs` is **not** the rolling upstream checkpoint; never replace it just to make an upstream merge pass.
6. After completing review and validation, set `.github/upstream-english.json` → `integrated_commit` to the exact upstream SHA incorporated by the merge. Keep all existing JSON fields. It must be an ancestor of the resulting English branch. Update TRANSLATION_STATUS with actual evidence, risks and exclusions. The monitor does not advance this field for you.
7. Commit the merge/translation/checkpoint and open a PR **inside this fork**:
   ```bash
   git add -- YOUR_REVIEWED_PATHS
   git commit
   git push -u origin sync/upstream-SHORT_SHA
   gh pr create --repo mehmetcanbudak/trading-hub \
     --base feat/english-localization --head sync/upstream-SHORT_SHA --draft
   ```
   Read the diff and macOS/Windows checks; finish missing translations and resolve failures. Merge only after human review. There is no automatic merge. Do not open a PR against the original upstream or include Rust/Swift work unintentionally.
8. The next monitor run sees the advanced checkpoint and reports remaining work or **Up to date**. A successful merge does not replace the installed app: update separately using the installation guide, with a backup and a fully quit app.

## Tests and operational limits

The tracker tests use temporary Git repositories and fake GitHub responses. They cover no-change/new-commit detection, history rewrites, invalid checkpoints, doc mappings, preserved notes, bounded reports, wrong-repository writes and retry-safe notification deduplication. No upstream source is executed by these tests.

GitHub schedules can be delayed/dropped under load. In public repositories, schedules are disabled after **60 days without repository activity**. Use Actions to re-enable/run the workflow if needed; this is a daily check, not a real-time webhook or guaranteed email service. Both the schedule and manual dispatch require the workflow on the default branch. Changes to repository/default-branch/Issues settings can stop operation.

Useful read-only checks:

```bash
gh run list --repo mehmetcanbudak/trading-hub --workflow upstream-english-tracking.yml
gh issue list --repo mehmetcanbudak/trading-hub --state all --search 'Upstream synchronization tracker'
```

GitHub behavior references: [scheduled events](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#schedule) and [workflow token permissions](https://docs.github.com/en/actions/security-for-github-actions/security-guides/automatic-token-authentication).

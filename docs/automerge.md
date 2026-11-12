# Auto-merge for docs PRs

`.github/workflows/automerge-docs.yml` turns on GitHub auto-merge for a pull request when:

- it is opened by the repository owner (not a draft), and
- every changed file is under `docs/` or is a Markdown file (`*.md`).

Auto-merge waits for the required checks, so a red PR never merges. Any other PR is left for review.

## One-time settings (owner only)

1. **Settings → General → Pull Requests**: tick *Allow auto-merge*.
2. **Settings → Branches → main → branch protection rule**: tick *Require status checks to pass before merging* and add `build-test` (the CI job). Without a required check GitHub merges at once.
3. Optional: tick *Require branches to be up to date* so a docs PR is retested after another merge.

## Notes

- The workflow runs on `pull_request_target` and only reads the list of changed files; it never checks out or runs code from the PR.
- Docs paths are ignored by the deploy workflow, so a merge made with the default token triggers nothing that is skipped.
- To stop it for one PR, turn auto-merge off on that PR, or open it as a draft.

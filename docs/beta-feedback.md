# Beta feedback loop (#98)

## How testers send feedback

Settings > Feedback > **Send feedback** opens the mail app with the app version and device (kind, OS, installed app or browser tab) filled in. The address comes from `Feedback:Email` in `src/Dialed.Web/wwwroot/appsettings.json`; while it is empty the button is replaced by a note. The GitHub repository is private, so testers do not file issues themselves.

Settings > Feedback > **Known issues** lists what is already known (strings `KnownIssues_*`; keep it short and update it when something is fixed).

## Weekly review

1. Once a week (Monday) read the feedback mail and the error reports (see `docs/observability.md`).
2. For each item create a GitHub issue with the `beta-feedback` label, quoting version and device; link the mail thread in the issue, not personal details.
3. Triage: bug (fix next wave), idea (backlog with the `beta-feedback` label), known issue (add to the list above), question (answer the tester).
4. Reply to the tester when it is fixed or decided.

The label exists as `beta-feedback` (created with this change).

# Project maintenance and GitHub sync

The user has authorized uploading this project and syncing subsequent completed changes to:

- Repository: https://github.com/sekaowa4/ecom-automat-hub
- Branch: main
- Project directory: mercado-repricing/

After each authorized source change, run relevant checks, review the publishable snapshot, then sync it to GitHub and verify the remote commit. This standing authorization does not authorize live price changes, other projects, or uploading private data.

Use `python scripts/prepare-github-snapshot.py` to prepare the source snapshot. The working directory also contains production data: never upload the whole working directory or rely only on `.gitignore`. Do not publish artifacts, databases, uploaded operational price sheets, logs, screenshots, browser profiles, credentials, tokens, private test reports, runtime folders, installers, or user-supplied executables. Keep raw local records unchanged.

Read the latest remote main commit before syncing. Preserve other projects and their changes. Update only this project's directory and relevant hub navigation. Never force-push or overwrite unrelated files. If the branch changed meanwhile, reconcile with the new head before retrying. Use the connected GitHub tools if local Git authentication is unavailable; never extract or log credentials.

Record the verified commit and uploaded source manifest under local `artifacts/github-sync/`. Report a failed sync honestly. GitHub sync is source backup and version control; it is not deployment or a background watcher.

Keep existing business rules and submission checkpoints. Do not change table targets, other promotions, or price scope just to remove NeedsReview results. Never run same-store operations concurrently. Verify ambiguous saves before retrying.

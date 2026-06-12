# IAM policy templates

Four JSON snippets used during AWS setup. Read [`../../docs/aws-setup.md`](../../docs/aws-setup.md) for the click-by-click context — these files are referenced from there.

| File | Attach to | What it allows |
|---|---|---|
| `api-github-actions-trust.json` | Role `psr-service-github-actions` (trust policy) | GitHub Actions in `<owner>/psr-service-api` on branch `master` can assume the role via OIDC |
| `api-github-actions-permissions.json` | Same role (inline policy) | Push images to the `psr-service-api` ECR repo |
| `wpf-github-actions-trust.json` | Role `psr-service-wpf-github-actions` (trust policy) | GitHub Actions in `<owner>/psr-service-wpf` on any `v*` git tag can assume the role |
| `wpf-github-actions-permissions.json` | Same role (inline policy) | Write objects to `s3://psr-service-releases` |

Before applying any of them, do a global find/replace:
- `REPLACE_ACCOUNT_ID` → your 12-digit AWS account ID
- `REPLACE_GITHUB_OWNER` → your GitHub username or org name

(The OIDC provider itself — `token.actions.githubusercontent.com` — should already exist in your AWS account from the sales setup. If not, see step 5 in `aws-setup.md`.)

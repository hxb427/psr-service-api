# IAM policy templates

Seven JSON snippets used during AWS setup. Read [`../../docs/aws-setup.md`](../../docs/aws-setup.md) for the click-by-click context — these files are referenced from there.

| File | Attach to | What it allows |
|---|---|---|
| `api-github-actions-trust.json` | Role `psr-service-github-actions` (trust policy) | GitHub Actions in `<owner>/psr-service-api` on branch `master` can assume the role via OIDC |
| `api-github-actions-permissions.json` | Same role (inline policy) | Push images to the `psr-service-api` ECR repo |
| `wpf-github-actions-trust.json` | Role `psr-service-wpf-github-actions` (trust policy) | GitHub Actions in `<owner>/psr-service-wpf` on any `v*` git tag can assume the role |
| `wpf-github-actions-permissions.json` | Same role (inline policy) | Write objects to `s3://psr-service-releases` |
| `field-portal-github-actions-trust.json` | Role `psr-field-portal-github-actions` (trust policy) | GitHub Actions in `<owner>/field-portal-android` on any `v*` git tag can assume the role |
| `field-portal-github-actions-permissions.json` | Same role (inline policy) | Write objects to `s3://psr-field-portal-releases` |
| `api-s3-field-portal-read.json` | EC2 instance role `psr-ec2-ecr-read` (inline policy) | Let the API presign GETs for the field portal APK — without it the updater's download URL 403s |

Before applying any of them, do a global find/replace:
- `REPLACE_ACCOUNT_ID` → your 12-digit AWS account ID
- `REPLACE_GITHUB_OWNER` → your GitHub username or org name

The three `field-portal-*` / `api-s3-*` files need no find/replace — they are already filled in
for this account. Every file here carries a `_comment` key for context; strip it before pasting,
since the IAM console rejects unknown top-level keys.

(The OIDC provider itself — `token.actions.githubusercontent.com` — should already exist in your AWS account from the sales setup. If not, see step 5 in `aws-setup.md`.)

The three `field-portal-*` / `api-s3-*` files belong to the Android update system — see
[`../../docs/field-portal-updates-setup.md`](../../docs/field-portal-updates-setup.md) for the
click-by-click context, including how to read the real OIDC subject out of CloudTrail rather
than guessing at it.

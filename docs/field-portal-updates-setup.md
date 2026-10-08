# AWS setup — Field Portal updates

Click-by-click setup for the Android field portal's release pipeline and in-app updater.
Companion to [`aws-setup.md`](./aws-setup.md), which covers the API itself; this adds only what
the update system needs. IAM templates live in [`../deploy/iam/`](../deploy/iam/).

Account `423693203837`, region `ap-south-1`, GitHub owner `hxb427`.

---

## How it fits together

Three moving parts, and it is worth knowing which does what before clicking:

| Part | What it does |
|---|---|
| **The floor** (`min_field_portal_version`) | Server-side. Any build below it gets 426 on every route including login. This is what makes an update *mandatory* — the app is useless until it updates. |
| **The feed** (`/app-versions`) | What the updater reads: newest published build for this client, plus a download URL. Exempt from the floor, so a blocked phone can still fetch its way out. |
| **The bucket** | Holds the signed APK. **Private.** The API signs a short-lived GET for each download; phones never authenticate to S3. |

The bucket being private is the one real difference from the WPF Velopack feed in `aws-setup.md`
Step 4, which is public-read because end-user machines read it directly. Here the APK carries the
pinned cert thumbprint and everything else compiled into the build, so a leaked URL would hand
anyone the binary. Presigned URLs expire and are scoped to one object.

> **Order matters at the end, not the start.** Steps 1–5 are safe in any order and change nothing
> for anyone. Step 8 — raising the floor — is the one that can strand people, and it must come
> after a release has actually gone out and been taken.

---

## Step 1 — Create the releases bucket

**Console → S3 → Create bucket**

- Name: `psr-field-portal-releases` (globally unique — if taken, add a suffix and update
  `S3:Bucket` in `appsettings.json`, the `S3_BUCKET` env in the app's `release.yml`, and both
  `field-portal-*` IAM templates)
- Region: `ap-south-1`
- Object Ownership: ACLs disabled
- **Block all public access: LEAVE CHECKED.** Unlike the WPF bucket, this one stays private.
- Versioning: Disabled
- Encryption: SSE-S3 (default)

No bucket policy. Access comes from the two IAM roles below.

---

## Step 2 — Let the API read the bucket

The API signs the download URLs, and **a presigned URL carries the permissions of whoever signed
it**. Without this grant the feed returns a URL that 403s on the phone while the API logs nothing
wrong — which is a genuinely annoying thing to debug.

**Console → EC2 → the API instance → Security → IAM role** (`psr-ec2-ecr-read`)
→ **Add permissions → Create inline policy → JSON**

Paste [`deploy/iam/api-s3-field-portal-read.json`](../deploy/iam/api-s3-field-portal-read.json).
Name it `psr-field-portal-releases-read`. Create.

No API restart needed — the SDK picks up instance-role credentials per request.

---

## Step 3 — Create the GitHub Actions role for the app repo

**Console → IAM → Roles → Create role → Custom trust policy**

Paste [`deploy/iam/field-portal-github-actions-trust.json`](../deploy/iam/field-portal-github-actions-trust.json),
replacing `REPLACE_ACCOUNT_ID` with `423693203837` and the owner/repo ID placeholders as described
below. Name the role `psr-field-portal-github-actions`.

Then **Add permissions → Create inline policy → JSON** and paste
[`field-portal-github-actions-permissions.json`](../deploy/iam/field-portal-github-actions-permissions.json).

### Getting the OIDC subject right

This is the step that bites. GitHub sends subjects with **immutable numeric IDs** appended:

```
repo:hxb427@<OWNER_ID>/field-portal-android@<REPO_ID>:ref:refs/tags/v0.2.0
```

A trust policy written against plain names is denied with `Not authorized to perform
sts:AssumeRoleWithWebIdentity`, and nothing in the error says why. Do not guess the IDs — read
the real subject:

1. Create the role with the trust policy as-is (placeholders intact; it will not match yet).
2. Push a tag so the workflow runs and fails at **Configure AWS credentials**.
3. **CloudTrail → Event history → Event name `AssumeRoleWithWebIdentity`** → open the failed event
   → copy `userIdentity.userName`. That is the exact subject.
4. Paste it into the trust policy, replacing everything up to `:ref:` and leaving `:ref:refs/tags/v*`
   wildcarded.

The IDs never change, so pinning them survives a repo or org rename.

---

## Step 4 — Create the Android signing keystore

One keystore signs every build a technician installs. Android refuses to upgrade an app with one
signed by a different key, so **if this is lost, no existing install can ever be updated again** —
every phone needs a manual uninstall and reinstall. Back it up somewhere you trust, off this machine.

```bash
keytool -genkey -v -keystore upload-keystore.jks \
  -keyalg RSA -keysize 2048 -validity 10000 -alias upload
```

It prompts for a store password, a key password and a name/org. Keep the passwords.

Then base64 it for the GitHub secret — in PowerShell:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("upload-keystore.jks")) | Set-Clipboard
```

The encoded value should be roughly 3–4 KB. The release workflow refuses anything under 1000
characters, because a truncated paste is the usual failure and it is better caught loudly.

**Do not commit the keystore.** `.gitignore` in the app repo already blocks `*.jks` and
`key.properties`.

---

## Step 5 — Add the GitHub secrets

**github.com/hxb427/field-portal-android → Settings → Secrets and variables → Actions**

| Secret | Value |
|---|---|
| `AWS_ROLE_TO_ASSUME` | ARN of the role from Step 3 (`arn:aws:iam::423693203837:role/psr-field-portal-github-actions`) |
| `ANDROID_KEYSTORE_BASE64` | The base64 blob from Step 4 |
| `ANDROID_KEYSTORE_PASSWORD` | Store password |
| `ANDROID_KEY_PASSWORD` | Key password |
| `ANDROID_KEY_ALIAS` | `upload` |
| `API_BASE_URL` | `https://13.207.24.101` |
| `API_ADMIN_USERNAME` | An admin login — registering a release is admin-only |
| `API_ADMIN_PASSWORD` | That account's password |

The API account needs the **admin** role specifically; manager is not enough for publishing.

---

## Step 6 — Cut the first release

Releases are tag-triggered, and the tag must match `pubspec.yaml`. For `version: 0.2.0+2`:

```bash
git tag -a v0.2.0 -m "0.2.0 — first published field portal build"
git push origin v0.2.0
```

The workflow builds and signs the APK, uploads it to `s3://psr-field-portal-releases/v0.2.0/app-release.apk`,
registers it with the API, and checks the feed reports it.

Bump **both** halves of the pubspec version each release. The `+N` build number is what the
updater and Android compare; two releases sharing one `+N` cannot replace each other, and the API
rejects the duplicate rather than letting you find out on a phone.

---

## Step 7 — Verify

```bash
curl -sk "https://13.207.24.101/app-versions/latest?clientId=field-portal"
```

Expect the version, build number and a presigned `downloadUrl`. Then actually fetch it — this is
what proves Step 2 worked:

```bash
curl -s -o /dev/null -w "%{http_code}\n" "<the downloadUrl>"
```

`200` means the API's instance role can read the bucket. `403` means it cannot — go back to Step 2.

On the phone: **Settings → App version** shows the running build and the updater's state, and
**Check for updates** forces a re-poll. First install prompts for "allow installs from this app";
that is Android's own one-time permission screen, not something to work around.

---

## Step 8 — Only now, consider the floor

Everything above is additive and strands nobody. Raising the floor is not.

A phone below the floor cannot log in, and the in-app updater is its only way back. So raise it
only once the release is published **and** technicians have actually taken it — the updater nags
them with a banner, and marking a release `mandatory` hardens that nag without locking anyone out.
The floor is the last resort, for when an old build must stop talking to the server at all.

**Desktop app → Settings → Minimum allowed field portal version** (admin only), or:

```bash
curl -sk -X PUT https://13.207.24.101/settings \
  -H "Authorization: Bearer <admin jwt>" \
  -H "X-Client-Id: wpf" -H "X-Client-Version: 1.2.6" \
  -H "Content-Type: application/json" \
  -d '{"invoiceGenerationEnabled":true,"minFieldPortalVersion":"0.2.0"}'
```

Set it back to `0.0.0` to lift it. The gate caches each floor for 60s, but the settings write
evicts the cache, so a change applies at once.

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `Not authorized to perform sts:AssumeRoleWithWebIdentity` | Trust policy written against repo *names* | Read the real subject from CloudTrail — Step 3 |
| `downloadUrl` returns 403 on the phone | API's instance role cannot read the bucket | Step 2 |
| `/app-versions/latest` returns 404 | Nothing published for this client yet | Normal before the first release; the app reads it as "up to date" |
| Release job fails registering with 409 | That build number is already registered | Bump the `+N` in pubspec and retag |
| Update banner never appears | Built with `--split-per-abi`, which offsets versionCode per ABI | Universal APK only — `release.yml` says so and does not pass the flag |
| Install fails with a signature mismatch | APK signed with a different key than the installed build | The keystore from Step 4 must be the same one, every release, forever |
| Phone stuck on "Update required" with "no newer build published" | Floor raised ahead of a release | Publish the release, or set the floor back to `0.0.0` |

# AWS setup — PSR Service API

Adds the PSR Service API to the **existing AWS infrastructure** already provisioned for `psr-sales-api`. Most of the heavy lifting (VPC, SGs, EC2, RDS, OIDC provider) was done during the sales setup; this doc only covers the **delta** — the additions specific to PSR Service.

**Region:** `ap-south-1` (Mumbai) — same as sales.

**Final state after this doc:**
- One additional Docker container running on the existing EC2 (`/opt/psr/service-api/`)
- One additional MySQL database on the existing RDS (`psr_service`)
- One additional ECR repo (`psr-service-api`)
- One additional IAM role for GitHub Actions OIDC (`psr-service-github-actions`)
- One additional S3 bucket (`psr-service-releases`) for WPF Velopack releases — set up now, used later
- One additional IAM role for the future WPF release pipeline (`psr-service-wpf-github-actions`)

**Combined infra cost** (PSR + Sales) after the 6-month $200 AWS credit window: ~$34/mo. No change from current.

---

## Step 0 — Prerequisites

- AWS Console signed in as your admin IAM user (NOT root). Region selector top-right = **Asia Pacific (Mumbai) ap-south-1**.
- You have the EC2 SSH key (`psr-deploy.pem` or similar) used for `psr-sales-api` saved locally — we'll reuse it.
- You know:
  - **EC2 Elastic IP** (the sales `EC2_HOST` secret): `13.207.24.101` (or current value)
  - **RDS endpoint**: from RDS console → `psr-service-db` (or whatever the sales-era name was)
  - **AWS account ID**: top-right account menu, copy the 12-digit number
  - **Your GitHub username/org** (this will host the new repo)
- The OIDC identity provider `token.actions.githubusercontent.com` already exists in IAM (created during sales setup). Verify: **IAM → Identity providers** → should be in the list.

---

## Step 1 — Open port 443 on the existing SG

The sales API binds to **8443**; PSR Service will bind to **443**. The EC2 security group (`psr-api-sg` or whatever it's named in your account) needs port 443 added.

1. **Console → EC2 → Security Groups → `psr-api-sg`** (the SG attached to your EC2 instance)
2. **Inbound rules → Edit inbound rules → Add rule:**
   - Type: **HTTPS**
   - Port range: **443**
   - Source: `0.0.0.0/0` (in-house clients across the public internet)
   - Description: `PSR Service API`
3. Save.

Confirm port 8443 is also still there (sales API). Don't remove it.

---

## Step 2 — Create the database + DB user on existing RDS

We add a fresh DB and a least-privilege user for it. Run from EC2 (RDS has no public access).

SSH to EC2:
```powershell
ssh -i path\to\psr-deploy.pem ec2-user@13.207.24.101
```

Make sure the mysql client is installed (AL2023 ships `mariadb105` instead of the upstream `mysql` package):
```bash
sudo dnf install -y mariadb105
```

Connect to RDS as the master user (use the password you saved during sales setup):
```bash
mysql -h <RDS_ENDPOINT> -u admin -p
```

Then in the MySQL shell:
```sql
CREATE DATABASE psr_service CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER 'psr_app'@'%' IDENTIFIED BY 'GENERATE_A_STRONG_PASSWORD_HERE';
GRANT ALL PRIVILEGES ON psr_service.* TO 'psr_app'@'%';
FLUSH PRIVILEGES;
EXIT;
```

Save the `psr_app` password — you'll paste it into the EC2 `.env` in Step 5.

---

## Step 3 — Create the ECR repository

**Console → ECR → Private registry → Repositories → Create repository**

- Visibility: **Private**
- Name: `psr-service-api`
- **Tag immutability: DISABLED** (we overwrite the `:latest` tag every deploy — sales hit this gotcha and had to flip it back)
- Image scan settings: **Scan on push enabled**

After creation, click the repo → **Lifecycle Policy → Create rule:**
- Rule priority: 1
- Description: "Keep last 7 images"
- Image status: Any
- Match criteria: Image count more than 7
- Tag status: Any
- Action: expire

Save. ECR will auto-delete images older than the 7 most recent.

---

## Step 4 — Create the S3 bucket for WPF releases

This is for the **future** WPF Velopack feed. Set up now so the IAM role created in Step 5 can reference it.

**Console → S3 → Create bucket**
- Name: `psr-service-releases` (globally unique — if taken add a company suffix, e.g. `psr-service-releases-poornasree`)
- Region: `ap-south-1`
- Object Ownership: ACLs disabled
- **Block all public access: UNCHECK** (and confirm the warning — Velopack feeds are read by end-user machines, must be public-read)
- Versioning: Disabled
- Encryption: SSE-S3 (default)

After creation, **bucket → Permissions → Bucket policy → Edit → paste:**
```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Sid": "PublicRead",
    "Effect": "Allow",
    "Principal": "*",
    "Action": "s3:GetObject",
    "Resource": "arn:aws:s3:::psr-service-releases/*"
  }]
}
```
Save. (If you used a different bucket name, edit the Resource ARN.)

---

## Step 5 — Create the GitHub Actions IAM roles

Two roles — one for the API CI/CD (now), one for the WPF release pipeline (later, but set up now so the bucket has a designated writer).

### 5a — API role

**Console → IAM → Roles → Create role:**
- Trusted entity type: **Web identity**
- Identity provider: **token.actions.githubusercontent.com** (already exists from sales)
- Audience: **sts.amazonaws.com**
- GitHub organization: your GitHub username/org
- GitHub repository: `psr-service-api`
- GitHub branch: `master`
- **Don't attach any AWS managed policies** — we'll add an inline policy.
- Role name: `psr-service-github-actions`
- Description: "GitHub Actions OIDC role for psr-service-api ECR pushes"

Create the role. Then:

1. Open the role → **Trust relationships → Edit trust policy** → paste contents of `deploy/iam/api-github-actions-trust.json` after replacing `REPLACE_ACCOUNT_ID` and `REPLACE_GITHUB_OWNER`.
2. **Permissions → Add permissions → Create inline policy → JSON tab** → paste contents of `deploy/iam/api-github-actions-permissions.json` after the same replacements. Name it `EcrPushToServiceApi`.

Copy the **role ARN** — you'll save it as a GitHub secret in Step 7.

### 5b — WPF role (for later)

Same flow:
- Trusted entity: Web identity, same OIDC provider
- GitHub organization: your username/org
- GitHub repository: `psr-service-wpf`
- **Skip the branch field** — instead we'll restrict to tags via the trust policy
- Role name: `psr-service-wpf-github-actions`
- Don't attach managed policies

After creation:
1. **Trust relationships → Edit** → paste `deploy/iam/wpf-github-actions-trust.json` (replaces `REPLACE_*`). This locks the role to tag pushes matching `v*` only (no random branch pushes can publish a release).
2. **Permissions → Add inline policy** → paste `deploy/iam/wpf-github-actions-permissions.json`. Name it `WriteReleasesBucket`.

Copy this role ARN too — save it somewhere for when the WPF repo exists.

---

## Step 6 — Prep the EC2 directory + cert

SSH to EC2 (same key as sales). Create the new service's directory tree:
```bash
sudo mkdir -p /opt/psr/service-api/{certs,logs}
sudo chown -R ec2-user:ec2-user /opt/psr/service-api
cd /opt/psr/service-api
```

Generate the self-signed cert. The script needs to be on the box — easiest is to copy from your laptop:
```powershell
# from your laptop, in psr-service-api/
scp -i path\to\psr-deploy.pem deploy/scripts/generate-cert.sh ec2-user@13.207.24.101:/opt/psr/service-api/
```

Back on EC2:
```bash
cd /opt/psr/service-api
bash generate-cert.sh 13.207.24.101 ./certs
```

Save the printed **password** and **SHA-256 thumbprint** somewhere — you'll need:
- The **password** in the next step's `.env`
- The **thumbprint** when the WPF client is built (it pins this in `appsettings.json`)

Confirm permissions:
```bash
ls -l certs/
# psr.crt -> 644
# psr.key -> 600
# psr.pfx -> 644   <-- must NOT be 600, container app user must read it
```

Create the `.env` file (gitignored, lives only on the EC2 box):
```bash
nano .env
```

Paste and fill in:
```env
# Pinned ECR image — CI overwrites this on each deploy
API_IMAGE=<YOUR_ACCOUNT_ID>.dkr.ecr.ap-south-1.amazonaws.com/psr-service-api:latest

# RDS connection (use the psr_app user + password from Step 2)
ConnectionStrings__Default=Server=<RDS_ENDPOINT>;Port=3306;Database=psr_service;User=psr_app;Password=<PASSWORD_FROM_STEP_2>;SslMode=Required;TreatTinyAsBoolean=true;AllowUserVariables=true

# JWT signing key — run `openssl rand -base64 48` and paste here
Jwt__Signing=<32+_CHAR_RANDOM_STRING>
Jwt__Issuer=psr-service
Jwt__Audience=psr-service-wpf
Jwt__ExpiryHours=24

# Cert password from generate-cert.sh output
Kestrel__Endpoints__Https__Certificate__Password=<PASSWORD_FROM_STEP_6>

# Admin seed password (first-run only; admin must change on first login)
SEED_ADMIN_PASSWORD=<CHOSEN_FIRST_LOGIN_PASSWORD>

ASPNETCORE_ENVIRONMENT=Production
```

Save (`Ctrl+O`, Enter, `Ctrl+X`). Tighten:
```bash
chmod 600 .env
```

---

## Step 7 — Create the GitHub repo + secrets

1. Create a new GitHub repo `psr-service-api` (private). Default branch: **master** (Settings → Branches → Default branch).
2. On your laptop, init + push:
   ```powershell
   cd C:\Users\harig\OneDrive\Documents\workspace-claude\psr-service-api
   git init -b master
   git add .
   git commit -m "Initial commit"
   git remote add origin git@github.com:<YOUR_USERNAME>/psr-service-api.git
   git push -u origin master
   ```
3. **Repo → Settings → Secrets and variables → Actions → New repository secret** — add four secrets:

| Name | Value |
|---|---|
| `AWS_ROLE_TO_ASSUME` | The `psr-service-github-actions` role ARN from Step 5a |
| `EC2_HOST` | `13.207.24.101` (or current EIP) |
| `EC2_USER` | `ec2-user` |
| `EC2_SSH_KEY` | The entire contents of `psr-deploy.pem` (or whichever key you use for the EC2 box) |

The push to master in step 2 will have already triggered `.github/workflows/deploy.yml`. It will fail at the **Configure AWS credentials** step until the secrets are saved — that's expected. Once secrets are in place, re-run via **Actions → Deploy API → Re-run all jobs**.

---

## Step 8 — Watch the first deploy

**Repo → Actions → Deploy API → latest run.** Steps in order:
1. Checkout ✓
2. Setup .NET 10 ✓
3. Restore + test ✓ (5 unit tests)
4. AWS OIDC ✓ (this is the one that fails first if your IAM trust policy is wrong — check the `sub` claim format)
5. ECR login ✓
6. Build + push image ✓ (~3 min first time, cached after)
7. SSH setup ✓
8. Upload `docker-compose.yml` to `/opt/psr/service-api/`
9. SSH `docker compose pull && up -d`
10. Smoke test — `curl -fk https://13.207.24.101/health`

Expected smoke-test response:
```json
{"status":"ok","version":"1.0.0.0","uptimeSeconds":3,"dbConnected":true,"serverTimeUtc":"..."}
```

On the EC2 box (still SSH'd in), confirm logs:
```bash
docker compose -f /opt/psr/service-api/docker-compose.yml logs api --tail 50
```

Look for the `ADMIN USER SEEDED` warning on the very first run — this is your admin password reminder.

---

## Step 9 — Verify auth end-to-end

From your laptop:
```powershell
curl.exe -k -X POST https://13.207.24.101/auth/login `
  -H "Content-Type: application/json" `
  -d "{\"username\":\"admin\",\"password\":\"<SEED_ADMIN_PASSWORD>\"}"
```

Response should include a JWT token and `"mustChangePassword": true`. The change-password call:
```powershell
$token = "<paste JWT from above>"
curl.exe -k -X POST https://13.207.24.101/auth/change-password `
  -H "Authorization: Bearer $token" `
  -H "Content-Type: application/json" `
  -d "{\"currentPassword\":\"<SEED>\",\"newPassword\":\"<NEW_STRONG_PASSWORD>\"}"
```

Returns a new JWT (and silently bumps `token_version`, kicking out the old one). The admin's `must_change_password` flag is now cleared.

You're live.

---

## Troubleshooting (lessons learned from sales)

| Symptom | Root cause | Fix |
|---|---|---|
| OIDC AssumeRole fails with "InvalidIdentityToken" | `sub` condition in trust policy doesn't match the runner's actual sub | Check exact format: `repo:OWNER/psr-service-api:ref:refs/heads/master`. No typos. |
| ECR push fails with "image tag immutable" | ECR repo created with tag immutability ON | ECR console → repo → Properties → Edit → Tag immutability: Mutable |
| Container stuck on `(health: starting)` forever | `curl` missing from `aspnet:10.0` image | Already fixed in our Dockerfile — confirm `apt-get install curl` is in the runtime stage |
| `docker compose up` fails immediately, logs show "permission denied" reading PFX | Cert PFX has 600 perms | `chmod 644 certs/psr.pfx` — our generate-cert.sh now does this automatically |
| Smoke test fails "Connection refused" | Container is still binding when test runs | Workflow already uses `--retry-connrefused --retry 10`. If still failing, container is crashing — check `docker compose logs api` |
| Healthcheck returns `dbConnected: false` | Connection string wrong, or RDS SG doesn't allow EC2 | Verify `psr-db-sg` inbound allows `psr-api-sg` on 3306; verify Step 2 user creation actually committed |
| `mysql: command not found` on EC2 | AL2023 doesn't ship `mysql` package | `sudo dnf install -y mariadb105` (provides `mysql` client) |
| `dnf install curl` fails on AL2023 | Conflicts with `curl-minimal` | Use `--allowerasing` or just don't install — `curl-minimal` is enough for shell scripts |
| GitHub Actions can't reach EC2 over SSH | EC2 SG SSH source is "My IP" only | Open SSH (22) to `0.0.0.0/0` — key-based auth is the actual boundary, IP filtering doesn't add real security on a public key system |
| First deploy works, second deploy fails to pull image | EC2 IAM role can't read ECR | Use existing `psr-ec2-ecr-read` role (from sales setup) — should be attached to the instance already. Verify in EC2 console → Security → IAM role. |

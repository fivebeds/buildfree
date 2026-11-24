# BuildFree (working name)

Free, hosted project management software for small contractors in San Diego, built for Contractors Helping Contractors Organization (https://www.chcorg.net/).

## Why this exists

Contractors Helping Contractors Organization (CHCO) is a 501(c)(3) public charity in National City, CA that supports local contractors and tradespeople in the San Diego region. Tools like Procore and Buildertrend are built for big companies and priced accordingly, which leaves many small contractors running jobs out of text messages and spreadsheets. This project is a free, hosted project management tool for those contractors: projects, clients, estimates, change orders, daily logs with photos, and a way for homeowners to see progress and approve changes.

I'm building it in public while I learn to code. Weekly notes live in the `devlog` folder.

## Milestone 1

Sign up and log in, create clients and projects, and post daily logs with job photos. Good enough to run on one real job. All of this works now: sign up, log in, clients, jobs, daily logs with photos, to-dos, team members with roles, estimates, change orders, and invoices with payments.

## Later

Estimate, change order and invoice approval by the homeowner, online payments, a simple schedule, a homeowner portal, and QuickBooks export.

## Stack

The backend is C# with ASP.NET Core Web API and Entity Framework Core. The database is PostgreSQL. Photos are saved as files through a small storage interface (`BuildFree.Api/PhotoStore.cs`): on disk for now, with S3-compatible storage (Cloudflare R2) planned for production behind the same interface. The frontend starts as plain JavaScript pages calling the REST API and will move to React later. Docker Compose runs the database locally.

## Run locally

You need the .NET SDK and Docker Desktop installed.

```
docker compose up -d
cd BuildFree.Api
dotnet run
```

The API starts at http://localhost:5000. Try it:

```
curl -X POST http://localhost:5000/api/auth/signup \
  -H "Content-Type: application/json" \
  -d '{"companyName":"Test Contracting","name":"Kyle","email":"kyle@example.com","password":"changeme123"}'
```

That returns a token. Send it as `Authorization: Bearer <token>` to `/api/projects` and `/api/clients`.

## Run the web app

With the API running, open `web/index.html` in your browser. Create an account on the sign up screen, then add clients and projects. The page talks to the API at `http://localhost:5000`, which you can change at the top of the script when the API is hosted online.

## API

All endpoints except sign up and log in need `Authorization: Bearer <token>`. Every company only ever sees its own data.

| Method | Path | What it does |
|---|---|---|
| POST | `/api/auth/signup` | Create a company and its first user, returns a token |
| POST | `/api/auth/login` | Log in, returns a token |
| GET, POST | `/api/clients` | List or add clients |
| PUT, DELETE | `/api/clients/{id}` | Edit or delete a client (their jobs are kept) |
| GET, POST | `/api/projects` | List or add jobs |
| GET, PUT, DELETE | `/api/projects/{id}` | Get, edit or delete a job. Deleting removes its logs, photos, to-dos and drafts; a job with approved or sent documents can't be deleted |
| PUT | `/api/projects/{id}/status` | Set status: 0 Planning, 1 Active, 2 On hold, 3 Complete |
| GET, POST | `/api/projects/{id}/logs` | List or add daily logs for a job |
| PUT, DELETE | `/api/logs/{id}` | Edit or delete a daily log (deleting removes its photos) |
| GET, POST | `/api/logs/{id}/photos` | List a log's photos, or upload photos (form upload, files in a field named `photos`, optional `caption`) |
| GET | `/api/projects/{id}/photos` | Every photo on a job, newest first |
| GET | `/api/photos/{id}` | The image itself (needs the token like every other call) |
| PUT, DELETE | `/api/photos/{id}` | Change a photo's caption, or delete it |
| POST | `/api/projects/{id}/todos` | Add a to-do to a job |
| GET | `/api/todos` | All to-dos for the company |
| PUT, DELETE | `/api/todos/{id}` | Edit or delete a to-do |
| POST | `/api/todos/{id}/toggle` | Mark a to-do done or not done |
| GET | `/api/me` | Who is logged in: name, email, role and company |
| GET | `/api/team` | Everyone on the team, plus invites that haven't been used yet |
| POST | `/api/team/invites` | Owner only. Invite someone by name, email and role; returns a one-time join code |
| DELETE | `/api/team/invites/{id}` | Owner only. Cancel an invite |
| PUT | `/api/team/{userId}/role` | Owner only. Change someone's role: 0 Owner, 1 Office, 2 Field |
| DELETE | `/api/team/{userId}` | Owner only. Remove someone; they're locked out right away and their logs and photos stay |
| GET | `/api/invites/{code}` | No login needed. What the join screen shows: company, name, email, role |
| POST | `/api/auth/accept-invite` | No login needed. Set a name and password from an invite code; returns a token |
| PUT | `/api/me/password` | Change your own password (current and new password). Returns a new token and logs you out on every other device |
| POST | `/api/team/{userId}/reset-link` | Owner only. Make a password reset code for someone on the team; it works once and expires in 24 hours |
| GET | `/api/password-resets/{code}` | No login needed. What the reset screen shows: company, name, email |
| POST | `/api/auth/reset-password` | No login needed. Set a new password from a reset code; returns a token |
| GET, POST | `/api/projects/{id}/estimates` | Owner and office only. List a job's estimates with totals, or create one |
| GET, PUT, DELETE | `/api/estimates/{id}` | Owner and office only. Get, save (details plus the full list of line items) or delete an estimate |
| PUT | `/api/estimates/{id}/status` | Set status: 0 Draft, 1 Sent, 2 Approved, 3 Declined |
| POST | `/api/estimates/{id}/copy` | Make a new draft with the same details and line items |
| GET, POST | `/api/projects/{id}/change-orders` | Owner and office only. List a job's change orders, or create one. Get, save, status, copy and delete use the `/api/estimates/{id}` endpoints above |
| GET | `/api/projects/{id}/contract` | Owner and office only. Original contract (latest approved estimate), approved and pending changes, revised total and days added |
| GET, POST | `/api/projects/{id}/invoices` | Owner and office only. List a job's invoices (with lines, payments and balance), or create one |
| GET | `/api/projects/{id}/billing` | Owner and office only. Contract total, invoiced, paid, balance due, left to bill, drafts and overdue invoices |
| GET, PUT, DELETE | `/api/invoices/{id}` | Get, save (details plus the full list of lines) or delete an invoice. Not allowed once it has payments |
| PUT | `/api/invoices/{id}/status` | Set status: 0 Draft, 1 Sent, 2 Void |
| POST | `/api/invoices/{id}/payments` | Record a payment: amount, date, method (0 Check, 1 Card, 2 Cash, 3 Zelle, 4 Bank transfer, 5 Other) and an optional reference |
| DELETE | `/api/payments/{id}` | Delete a payment entered by mistake |

## Photos

Photos attach to daily logs. The API accepts JPEG, PNG, GIF and WebP, checked by the file's contents rather than its name. Limits: 10 MB per photo, 10 photos per upload, 30 per log. The web app shrinks phone photos to at most 2,000 pixels on the long side before uploading (usually a few hundred KB), turns them upright, and uploads them in groups of 10.

Uploaded files go in `BuildFree.Api/photo-uploads/` by default (ignored by git). Set `Photos:Path` in configuration, or the `Photos__Path` environment variable, to use another folder. If your local database was created before photos existed, reset it once with `docker compose down -v` then `docker compose up -d` so the photos table gets created.

## Estimates

Each job can have estimates, numbered per company (#1, #2, #3...). Line items have a description, a category (labor, materials, subcontractor, equipment or other), a quantity, a unit and a unit cost. One markup applies to the whole estimate: the customer's unit price is cost plus markup, rounded to the cent. Sales tax applies to materials lines only.

The API returns each line's price and amount, plus the subtotal, tax, total, cost, profit and margin. The customer copy in the web app shows only prices, never cost or markup. Field crew can't see estimates.

An approved estimate is locked. To change it, make a copy, which starts a new draft. Deleting a job deletes its estimates.

## Change orders

Change orders cover work added or taken out after the estimate is approved. They work like estimates (same line items, markup, tax and print layout) with their own numbering (CO #1, #2...). A change order can add or remove days from the schedule, and a negative unit cost gives the customer a credit.

The job's contract total is the latest approved estimate plus every approved change order. Drafts and sent change orders show as waiting on approval. A printed change order shows the original contract, earlier approved changes, this change and the new contract total, with lines to sign.

## Invoices and payments

Each job can have invoices, numbered per company (#1, #2...). An invoice is a list of lines, such as a deposit, a percent of the contract or the remaining balance, and its total is the sum of the lines. Lines can be negative (for a credit) as long as the total is more than $0.

Payments are recorded against an invoice and can't be more than what's still owed. An invoice shows as draft, sent, partial, paid, overdue (sent, not fully paid and past its due date, in Pacific time) or void. Recording a payment on a draft marks it sent. Once an invoice has payments it can't be edited, voided or deleted; delete the payment first if it was entered by mistake.

The job's billing summary compares the revised contract (approved estimate plus approved change orders) with what's been invoiced and paid. Drafts and void invoices don't count as invoiced. The printed invoice shows the lines, payments received and the balance due. Field crew can't see invoices.

## Team members

Each person gets their own login. Whoever signs up is the company's owner. The owner invites people from the Team tab by name, email and role, and gets a join link to text or email them (BuildFree doesn't send email yet). The link works once and expires after 7 days. Only a fingerprint (SHA-256 hash) of the code is stored, so a lost link can't be shown again; the owner makes a new one instead.

| Role | Can do |
|---|---|
| Owner | Everything, including inviting people, changing roles and removing people |
| Office | Jobs, clients, daily logs, photos and to-dos. Can't manage the team |
| Field | Sees every job. Adds daily logs, photos and to-dos, and edits or deletes only the logs and photos they added |

The API checks the user on every request, so a role change or removal takes effect immediately, even though the old login token hasn't expired. A company always keeps at least one owner. Daily logs and photos record who added them.

If your local database was created before team members existed, reset it once with `docker compose down -v` then `docker compose up -d` so the new tables and columns get created.

## Security

- **Password guessing:** after 5 wrong passwords for one email in 15 minutes, logins for that email are refused until the 15 minutes are up. On top of that, one network gets 60 tries a minute at logging in, signing up, invite and reset links, and changing passwords (`RateLimits:AuthPerMinute`). Both reset when the API restarts.
- **Passwords:** 8 to 72 characters (BCrypt, which stores them, only uses the first 72 bytes). Logins take the same time whether or not the email is registered.
- **Forgotten passwords:** the owner opens the Team tab, clicks Reset password next to the person, and texts or emails them the link. It works once and expires in 24 hours. An owner who forgets their own password needs another owner to do this until BuildFree can send email.
- **Changing a password** (Team tab, Change password next to your name) or resetting it logs that person out on every other device. Every login token carries a fingerprint of the password, and the API checks it on each request.
- **Which websites can call the API:** set `Cors:Origins` to the web app's address. Outside development with nothing set, no other website can call it.
- Every response tells browsers not to guess file types, not to show the API inside another site, and not to send the page address along.

Before going online behind a hosting provider's proxy, the API has to be told to trust the proxy's forwarded address, or every visitor shares one network limit. That's part of hosting prep.

## Tests

`tests/smoke.sh` runs through every endpoint against a running API, using two test companies to check that one can never see or change the other's data. With the API running:

```
bash tests/smoke.sh
```

It needs `curl` and `jq`. GitHub runs it automatically on every pull request (see `.github/workflows/ci.yml`): it builds the API, starts it with a real PostgreSQL database, and runs the script. A green check on the pull request means everything passed.

## Database migrations

Until the project has a migration, the API creates its tables directly on startup. That's fine on your computer, but it can't change tables later without losing data, so the first migration has to exist before BuildFree goes online. After that, the API upgrades the database on startup and keeps existing data.

**Create a migration on GitHub (no .NET needed on your computer):** go to the Actions tab, pick "Create database migration", click "Run workflow", choose the branch with your model changes, type a name (`InitialCreate` for the first one, then names like `AddSchedule`), and run it. It generates the migration, starts the API with it on an empty PostgreSQL database, runs `tests/smoke.sh`, then pushes the files to a new branch and opens a pull request. If GitHub won't let the workflow open the pull request, turn on Settings > Actions > General > "Allow GitHub Actions to create and approve pull requests", or open one yourself from the branch it printed. Pull requests opened by the workflow don't start the regular checks by themselves; the workflow already ran them.

**Or on your computer:**

```
dotnet tool install --global dotnet-ef
cd BuildFree.Api
dotnet ef migrations add InitialCreate
```

Once a `Migrations` folder exists, the regular checks on every pull request fail if a model changed without a new migration, so the online database can always be upgraded. If your local database was created before migrations, reset it once with `docker compose down -v` then `docker compose up -d`.

## Keeping records safe

- A job with an approved estimate or change order, or an invoice that went out (sent, paid or void), can't be deleted. Mark it Complete instead. Jobs with only drafts can still be deleted.
- Approved estimates and change orders can't be deleted, and sent invoices can't be deleted (void them instead). Invoices with payments can't be edited, voided or deleted.
- Estimate, change order and invoice numbers stay unique even when two people create one at the same moment; the API quietly picks the next number and tries again.
- Unexpected errors come back as a short message (`{"error": "..."}`) and the details go to the API log. Requests the API can't read, like broken JSON, get a clear 400.

## Configuration

`Jwt:Key` in `appsettings.json` is a sample for local development only. Outside development the API refuses to start with it; set a real random key of 32+ characters with the `Jwt__Key` environment variable. Changing the key logs everyone out and makes every reset link stop working.

`Cors:Origins` (or `Cors__Origins`) lists the web addresses allowed to call the API, separated by commas, for example `https://app.buildfree.org`. `RateLimits:AuthPerMinute` (or `RateLimits__AuthPerMinute`) changes the per-network limit on login and password tries (60 by default).

## Status

Early. Nothing is production ready. Milestone 1 is feature complete; next is hosting it online so a real job can use it.

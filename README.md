# BuildFree (working name)

Free, hosted project management software for small contractors in San Diego, built for Contractors Helping Contractors Organization (https://www.chcorg.net/).

## Why this exists

Contractors Helping Contractors Organization (CHCO) is a 501(c)(3) public charity in National City, CA that supports local contractors and tradespeople in the San Diego region. Tools like Procore and Buildertrend are built for big companies and priced accordingly, which leaves many small contractors running jobs out of text messages and spreadsheets. This project is a free, hosted project management tool for those contractors: projects, clients, estimates, change orders, daily logs with photos, and a way for homeowners to see progress and approve changes.

I'm building it in public while I learn to code. Weekly notes live in the `devlog` folder.

## Milestone 1

Sign up and log in, create clients and projects, and post daily logs with job photos. Good enough to run on one real job. Right now the API handles sign up, log in, clients, and projects.

## Later

Estimates and proposals, change orders with client approval, a simple schedule, a homeowner portal, and QuickBooks export.

## Stack

The backend is C# with ASP.NET Core Web API and Entity Framework Core. The database is PostgreSQL. Photo storage will use S3-compatible storage (Cloudflare R2 in production) and gets added when daily logs are built. The frontend starts as plain JavaScript pages calling the REST API and will move to React later. Docker Compose runs the database locally.

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
| GET, PUT, DELETE | `/api/projects/{id}` | Get, edit or delete a job (deleting removes its logs and to-dos) |
| PUT | `/api/projects/{id}/status` | Set status: 0 Planning, 1 Active, 2 On hold, 3 Complete |
| GET, POST | `/api/projects/{id}/logs` | List or add daily logs for a job |
| PUT, DELETE | `/api/logs/{id}` | Edit or delete a daily log |
| POST | `/api/projects/{id}/todos` | Add a to-do to a job |
| GET | `/api/todos` | All to-dos for the company |
| PUT, DELETE | `/api/todos/{id}` | Edit or delete a to-do |
| POST | `/api/todos/{id}/toggle` | Mark a to-do done or not done |

## Tests

`tests/smoke.sh` runs through every endpoint against a running API, using two test companies to check that one can never see or change the other's data. With the API running:

```
bash tests/smoke.sh
```

It needs `curl` and `jq`. GitHub runs it automatically on every pull request (see `.github/workflows/ci.yml`): it builds the API, starts it with a real PostgreSQL database, and runs the script. A green check on the pull request means everything passed.

## Database migrations

Right now the API creates its tables directly on startup, which can't change tables later without losing data. To switch to migrations (one time, on your computer):

```
dotnet tool install --global dotnet-ef
cd BuildFree.Api
dotnet ef migrations add InitialCreate
```

Commit the new `Migrations` folder. From then on the API upgrades the database on startup and keeps existing data. When a model changes, run `dotnet ef migrations add <Name>` again. If your local database was created before migrations, reset it once with `docker compose down -v` then `docker compose up -d`.

## Configuration

`Jwt:Key` in `appsettings.json` is a sample for local development only. Outside development the API refuses to start with it; set a real random key of 32+ characters with the `Jwt__Key` environment variable.

## Status

Early. Nothing is production ready. Milestone 1 still needs photos on daily logs.

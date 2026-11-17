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

The backend is C# with ASP.NET Core Web API and Entity Framework Core. The database is PostgreSQL, with S3-compatible storage for photos (MinIO locally, Cloudflare R2 in production). The frontend starts as plain JavaScript pages calling the REST API and will move to React later. Docker Compose runs the database and storage locally.

## Run locally

You need the .NET SDK and Docker Desktop installed.

```
docker compose up -d
cd BuildFree.Api
dotnet run
```

The API starts on the port printed in the terminal. Try it:

```
curl -X POST http://localhost:5000/api/auth/signup \
  -H "Content-Type: application/json" \
  -d '{"companyName":"Test Contracting","name":"Kyle","email":"kyle@example.com","password":"changeme123"}'
```

That returns a token. Send it as `Authorization: Bearer <token>` to `/api/projects` and `/api/clients`.

## Status

Early. Nothing is production ready, and the dev database is recreated automatically with no migrations yet.

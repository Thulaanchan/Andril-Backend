# ANDRIL — Backend Starter Repository

**Status:** source-structure scaffold, not a completed ANDRIL service. Internal Modular Monolith + N-Tier layout is proposed pending M2/M5/M6 technical sign-off. Business rules and deployment providers remain subject to ANDRIL BRD v2.4-R1 and decision register.

## Scope

- One ASP.NET Core Web API serving Flutter **Android** and Angular Admin in separate GitHub repositories.
- Exactly three purpose-isolated product modules: **Learning, Hobbies, Social**.
- Shared technical projects (Groups, Communication, Economy, AI, etc.) are **not** extra product modules.
- Social requires 18+ explicit opt-in, mutual-match authorization for match-gated chat, and **Social-only MoodTone**.
- Mapbox is confirmed for the **Flutter Android map UI**. The reserved server-side `Integrations/Mapbox/` folder is optional and does not indicate an active Mapbox API integration.
- PostgreSQL/Supabase and private media storage are the data/storage direction; **not yet connected in this scaffold**.
- Do not reintroduce removed Learning Focus/Quizzes or Hobbies Events/Photo Portfolios.

## What is actually implemented

- All **11 source `.csproj` projects** and **4 xUnit project definitions** are registered in `Andril.slnx`.
- Source project references follow the proposed dependency direction: feature modules depend on Shared, Persistence depends on feature interfaces, API is the composition root.
- The API has a minimal valid `Program.cs` that exposes `GET /health` only.
- JSON configuration, CI workflow, build/test scripts, `.gitignore`, Docker example, and an OpenAPI **health-only draft** are included.
- All empty feature directories are tracked with `.gitkeep`, without fabricating class implementations.
- **Not included:** authentication, authorization handlers, EF Core, migrations, real APIs, AI/RTC/payment integrations, domain services, actual tests, secrets, or a verified production deployment.

## Local prerequisites

- .NET SDK 10, matching `global.json` (`10.0.400` or compatible feature roll-forward).
- NuGet connectivity for test project packages.

## Build and run on Windows PowerShell

```powershell
dotnet restore .\Andril.slnx
dotnet build .\Andril.slnx
dotnet test .\Andril.slnx
dotnet run --project .\src\Andril.API\Andril.API.csproj
```

Local starter endpoint: `http://localhost:5080/health` when running the HTTP launch profile (`dotnet run --launch-profile http --project ...`). Test project definitions contain **no actual test cases yet**; a passing test command alone does not demonstrate functional or security correctness.

## Folder ownership and change control

- M1 Learning, CV, Portfolio and shared AI Chat integration (lead pending on shared services).
- M2 identity, purpose-based privacy, wallet, payments and secure ledgers.
- M3 Hobbies, Map and Flight Points; shared XP lead pending.
- M4 chat, **Social-only MoodTone**, notifications, calls and live streaming.
- M5 Social, matching and confirmed Group Management backend ownership (Classrooms, Clubs, Zones).
- M6 Angular Admin, moderation, safety and QA/CI.

Before shipping, add server-side access checks to **all** protected routes, stable OpenAPI versions, RLS where appropriate, idempotent transactional ledgers, private storage policy tests, migration reviews and cross-repository compatibility gates.

## Moving from the existing ANDRIL local workspace

Your previously generated `Andril/backend` contains working generated projects. **This ZIP is a separate sample starter**, not a backup or an update to that directory. Do **not** overwrite your existing backend with this ZIP. Compare and selectively merge reviewed files after backup. Prefer preserving the already-built projects and their references.

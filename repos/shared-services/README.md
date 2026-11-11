# AppInHouse Shared Services

Shared infrastructure for the AppInHouse projects: email, Telegram and file storage, configured at run time (no deploy to change a
password or switch provider) and managed from a web portal. Design: `docs/architecture/shared_services_design.md`.

This is milestone 1 (TASK-032): the solution, the secret protection, the versioned configuration engine, the API host and the portal
shell. The email, Telegram and storage providers, API key management and the log screens come with the next tasks.

## Projects

| Project | Role |
|---|---|
| `Domain` | Entities and rules, no dependencies: `ChannelConfiguration` and its version rules, `ApiKey`, `StoredFile`, the four logs |
| `Application` | Use cases (`ChannelConfigManager`), validation, DTOs, and the interfaces the other layers implement (`ISecretProtector`, `IEmailProvider`, `ITelegramProvider`, `IStorageProvider`, repositories) |
| `Persistence` | EF Core: `SharedServicesDbContext` (schema `shared`, snake_case), repositories, PostgreSQL migration |
| `Infrastructure` | `DataProtectionSecretProtector` (ASP.NET Core Data Protection key ring) |
| `Api` | REST API, port **5030**: `GET /health`, `/api/v1/channel-configurations`, Swagger in development |
| `Web` | Management portal, port **5031**: Razor Pages and Bootstrap 5; talks to the API through the client SDK only |
| `ClientSdk` | Typed .NET client (`ISharedServicesClient`) with no reference to the service's own projects |

Dependencies point inwards: `Domain` references nothing, `Application` only `Domain`, `Persistence` and `Infrastructure` implement
`Application`'s interfaces, `Api` wires them together, `Web` only knows `ClientSdk`.

## Run

```powershell
dotnet build AppInHouse.SharedServices.sln
dotnet run --project src/Presentation/AppInHouse.SharedServices.Api   # http://localhost:5030/swagger
dotnet run --project src/Presentation/AppInHouse.SharedServices.Web   # http://localhost:5031
```

In development the API uses a SQLite file (`data/shared-services.db`, created on start), seeds a development API key and a few
made-up configurations, and the portal is configured with that key (`appsettings.Development.json` of both). Nothing else to install.

```powershell
dotnet test AppInHouse.SharedServices.sln
$env:SHARED_SERVICES_TEST_POSTGRES = "Host=localhost;Port=5432;Username=postgres;Password=postgres"   # optional: also run the PostgreSQL tests
dotnet test tests/AppInHouse.SharedServices.IntegrationTests
```

## Configuration

| Setting | Meaning |
|---|---|
| `Database:Provider` | `Sqlite` (default) or `Postgres` |
| `ConnectionStrings:SharedServicesDb` | The database. PostgreSQL: the tables are created by the migration in the `shared` schema |
| `Database:MigrateOnStartup` | Applies the migration (PostgreSQL) or creates the tables (SQLite) before the host starts listening |
| `DataProtection:ApplicationName` | Must be the same in every host that reads the same secrets; changing it makes stored secrets unreadable |
| `DataProtection:KeysPath` | Folder of the key ring. Back it up with the database, and mount it as a volume in a container |
| `Authentication:Authority` | Optional: the Identity STS, to accept bearer tokens as well as API keys |
| `SharedServices:Api:BaseUrl`, `SharedServices:Api:ApiKey` | (Web) where the API is and the key the portal uses |

## How the important rules are met

- **Secrets are never stored or shown in plain text.** A secret is protected with Data Protection before it is stored
  (`encrypted_secret`). DTOs have no property that can hold it; they carry `hasSecret` and the mask `••••••••`. Settings that look like
  a secret (a `password` or `token` property, a bot token, a PEM key) are refused.
- **Configurations are versioned.** Saving makes the next Draft; activating a version archives the active one and older drafts; a
  rollback goes back to the previous active version (or one you name) at once. Two people changing the same series at once cannot both win.
- **API keys** are shown once and stored as a SHA-256 hash with a short prefix; comparisons take constant time.

## Known limits (see the task report)

- The portal has **no sign-in yet**: anyone who can reach port 5031 can use it (it sees only masked secrets, but it can activate and roll
  back). Keep it on a trusted network until single sign-on with the Identity STS is added.
- No Docker files, no registration in the root `AppInHouse.sln`, and no send-test yet.

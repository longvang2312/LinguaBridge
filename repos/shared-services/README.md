# AppInHouse Shared Services

Shared infrastructure for the AppInHouse projects: **email, chat (Telegram, Discord, Slack) and file storage**, configured at run time
(no deploy to change a password or switch provider), with API keys per project, an audit trail, and a web portal to manage it all.
Design: `docs/architecture/shared_services_design.md`.

## Projects

| Project | Role |
|---|---|
| `Domain` | Entities and rules, no dependencies: `ChannelConfiguration` and its version rules, `ApiKey`, `StoredFile`, the four logs |
| `Application` | Use cases and validation: `ChannelConfigManager`, `EmailDispatcher`, `ChatDispatcher`, `StorageService`, `ApiKeyManager`, `AuditLogReader`; the interfaces the other layers implement |
| `Persistence` | EF Core: `SharedServicesDbContext` (schema `shared`, snake_case), repositories, PostgreSQL migration |
| `Infrastructure` | The providers (SMTP/SendGrid, Telegram/Discord/Slack, local disk/S3, mocks), the thumbnail service, `DataProtectionSecretProtector` |
| `Api` | REST API, port **5030**: `/health`, `/api/v1/...`, Swagger in development |
| `Web` | Management portal, port **5031**: Razor Pages and Bootstrap 5; talks to the API through the client SDK only |
| `ClientSdk` | Typed .NET client (`ISharedServicesClient`) with no reference to the service's own projects |

Dependencies point inwards: `Domain` references nothing, `Application` only `Domain`, `Persistence` and `Infrastructure` implement
`Application`'s interfaces, `Api` wires them together, `Web` only knows `ClientSdk`.

## Run on a developer machine

```powershell
dotnet build AppInHouse.SharedServices.sln
dotnet run --project src/Presentation/AppInHouse.SharedServices.Api   # http://localhost:5030/swagger
dotnet run --project src/Presentation/AppInHouse.SharedServices.Web   # http://localhost:5031
```

In development the API uses a SQLite file (`data/shared-services.db`, created on start), seeds a development API key
(`aih_live_devkey_...`, a platform key) and a few made-up configurations, and the portal is configured with that key
(`appsettings.Development.json` of both). Nothing else to install. Files go under `data/storage`.

```powershell
dotnet test AppInHouse.SharedServices.sln
$env:SHARED_SERVICES_TEST_POSTGRES = "Host=localhost;Port=5432;Username=postgres;Password=postgres"   # optional: also run the PostgreSQL tests
dotnet test tests/AppInHouse.SharedServices.IntegrationTests
```

## Run with Docker (production-like)

`docker-compose.shared-services.yml` is at the root of the repository. It starts PostgreSQL, the API and the portal.

```powershell
$env:SHARED_SERVICES_DB_PASSWORD = "<a long random password>"
$env:SHARED_SERVICES_ADMIN_KEY   = "aih_live_<40 or more random letters and digits>"   # the first API key, see below
docker compose -f docker-compose.shared-services.yml up -d --build
docker compose -f docker-compose.shared-services.yml ps          # wait for "healthy" on all three
```

Put the two values in an `.env` file next to the compose file (and keep it out of git), or in the server's environment. Compose refuses to
start without them. The API is then on `http://localhost:5030/health`, the portal on `http://localhost:5031`.
`SHARED_SERVICES_API_PORT` and `SHARED_SERVICES_WEB_PORT` move the published ports; the database is never published.

**Volumes (named, so they survive `docker compose down`; `down -v` deletes them):**

| Volume | Mounted at | What is lost without it |
|---|---|---|
| `shared-services-pgdata` | `/var/lib/postgresql/data` | all configurations, keys, logs, file records |
| `shared-services-dp-keys` | `/root/.aspnet/DataProtection-Keys` | **every stored secret becomes unreadable** (SMTP passwords, bot tokens, S3 keys) |
| `shared-services-storage` | `/data/storage` | the files of the local-disk provider |
| `shared-services-web-dp-keys` | the portal's `/root/.aspnet/DataProtection-Keys` | the forms that are open when the portal restarts |

Back up the database **and** `shared-services-dp-keys` together: a database without its key ring has secrets nobody can decrypt.

**The first API key.** A new database has no key, and keys are made with a key. `SHARED_SERVICES_ADMIN_KEY` solves that once: when
the database holds no key at all, the API makes a platform key with every scope from it (`SharedServices:Bootstrap:AdminApiKey`); the portal uses
the same value to call the API. Then, in the portal at `/ApiKeys`: create a key for each project (and a dedicated one for the portal),
switch the portal to its key (`SharedServices__Api__ApiKey`), remove `SHARED_SERVICES_ADMIN_KEY` from the environment and revoke
"Bootstrap admin key". Only the key's 15-character prefix is ever logged.

**Health checks.** Both containers check `http://localhost:<port>/health` (curl is installed in the image for that). The portal starts only
after the API is healthy, and the API only after PostgreSQL is. `Database:MigrateOnStartup` applies the migration (schema `shared`) before the
API listens, so a healthy API means a ready database.

## Using the API from another project

```csharp
services.AddSharedServicesClient(options =>
{
    options.BaseAddress = new Uri("http://localhost:5030");
    options.ApiKey = "aih_live_...";          // from configuration or a secret store, never from code
});
// then inject ISharedServicesClient: SendEmailAsync, SendChatMessageAsync, UploadFileAsync, ...
```

`ISharedServicesClient` covers configurations (list, get, save, activate, rollback), email (send, test, delivery logs), chat (send, test,
delivery logs), storage (upload, download, thumbnail, metadata, list, delete, test, media logs, queue a thumbnail rebuild), API keys (create, list,
revoke, rotate), the audit log, and the platform services: notifications (dispatch, templates, logs), background jobs (queues, retry, schedules),
webhooks (subscriptions, test, inbound sources), projects (list, create, update, usage) and metrics (overview, health status).

## The platform services

Eight services share one API. The first three (email, chat, storage) are described above; the rest sit on top of them.

| Service | Endpoints | In short |
|---|---|---|
| Notifications | `POST /api/v1/notifications/dispatch`, `GET .../templates`, `GET .../logs` | A template with `{{placeholders}}` sent by email, chat or both (side by side). A value that is missing is a **400**, never a half-filled message. Five stock templates: `UserRegistered`, `PasswordReset`, `SecurityAlert`, `SystemMaintenance`, `PaymentSuccess`. A **High** or **Critical** notification whose only channel fails is tried on the other one: a failed email gives chat a *summary* (subject and reason, never the body, which may carry a one-time link); a failed chat message gives the recipient the full email. With `"queue": true` the messages are queued (answer **202**) |
| Background jobs | `GET /api/v1/jobs/queues`, `POST .../queues/{name}/retry-failed`, `GET .../schedules`, `POST .../schedules/{id}/run` | Five queues (`email`, `chat`, `media-thumbnail`, `outbox`, `webhook`), each with its own workers (`System.Threading.Channels` wake them; the `background_jobs` table is the truth, so a restart loses nothing). A failed attempt waits **1 s, 5 s, 15 s** and tries again (four attempts); a failure that waiting cannot cure (no configuration, an exhausted quota, a 4xx from the application) fails at once. A job cut off by a restart is done again |
| Scheduled tasks | (same) | Cron in UTC: log retention (`0 2 * * *`, delivery, media, system and notification logs older than `Jobs:LogRetentionDays`; the audit log is never deleted), housekeeping (`30 2 * * *`, finished jobs and old inbound calls), metrics aggregation (`*/5 * * * *`). There are no temporary files to clean: an upload is held in memory and written once |
| Webhooks | `POST /api/v1/webhooks/incoming/{source}`, `GET/POST/DELETE .../subscriptions`, `POST .../subscriptions/{id}/test`, `GET .../inbound` | **Inbound**: `telegram` (secret token header), `github` (HMAC-SHA256 of the body), `stripe` (timestamp + HMAC, 5 minutes). A source without a secret in this installation refuses every call (**503**); a wrong signature is **401** and the body is not stored. **Outbound**: an application subscribes to `email.sent`, `email.failed`, `chat.sent`, `chat.failed`, `file.uploaded`, `webhook.received` (or `*`). Each post carries `X-AIH-Signature: sha256={hex HMAC-SHA256 of the exact body}`, `X-AIH-Event`, `X-AIH-Delivery` (the same id on every retry) and `X-AIH-Timestamp`; the signing key is shown once and stored only protected |
| Projects | `GET/POST /api/v1/projects`, `PUT .../{code}`, `GET .../{code}/usage` | Each application is a project with daily email and chat quotas and a storage quota (empty = no limit). Over the quota: **429** with `Retry-After` for the daily ones; a deactivated project gets **403**. Only delivered messages count; the day is the UTC day. Only the platform creates or changes projects |
| Metrics | `GET /api/v1/metrics/overview`, `GET .../health-status` | The last 24 hours across the eight services; each is **Healthy**, **Degraded** (more than a fifth of at least five operations failed; for jobs, any failed job) or **Idle** (nothing configured). Platform only |

A project key sees only its own subscriptions, notifications and usage; jobs, schedules, inbound sources and metrics are for the platform.

## API keys

- A key is `aih_live_` + 6 random characters + a 256-bit random secret. **The full key is shown once**, in the answer to the call that
  creates it (and in the portal's modal); only its SHA-256 and a 15-character prefix are stored, and it is compared in constant time.
- Keys can expire (30 days, 90 days, 1 year, or never) and be revoked; an expired or revoked key gets **401 at once**.
  "Last used" is recorded at most once a minute per key.
- **Rotation**: `POST /api/v1/api-keys/{id}/rotate?graceDays=7` (or the portal's button) makes a new key like the old one; both work during the
  grace period, then the old one stops. A project can hold several keys at once.
- Scopes: `config:read`, `config:write`, `email:send`, `chat:send`, `storage:upload` (also lets a key read its own project's files),
  `storage:read`, `storage:delete`.
- Only a **platform** key (project `default`) can create, revoke and rotate keys, and read the audit log: a project key that could mint keys
  could give itself any scope and outlive its own revocation. A project key may list its own project's keys.

## Project isolation

| Caller | May |
|---|---|
| Platform key (project `default`), or a bearer token | act on every project |
| Project key (for example `identity`, `linguabridge`) | send, upload, read files, read delivery and media logs, read and change configurations, and "test" **its own project's** resources; **403** (RFC 7807) on anything of another project |

This holds for every endpoint: configurations (list, get, save, activate, rollback), email/chat/storage test by configuration id, sends, uploads,
file reads and deletes, and the logs. A *public* file can be read by anyone; it still cannot be changed or deleted by another project.

## Configuration

| Setting | Meaning |
|---|---|
| `Database:Provider` | `Sqlite` (default) or `Postgres` |
| `ConnectionStrings:SharedServicesDb` | The database. PostgreSQL: the tables are created by the migration in the `shared` schema |
| `Database:MigrateOnStartup` | Applies the migration (PostgreSQL) or creates the tables (SQLite) before the host starts listening |
| `DataProtection:ApplicationName` | Must be the same in every host that reads the same secrets; changing it makes stored secrets unreadable |
| `DataProtection:KeysPath` | Folder of the key ring. Back it up with the database, and mount it as a volume in a container |
| `SharedServices:Bootstrap:AdminApiKey` | The first key (see above). Ignored once the database has a key |
| `Storage:BasePath` | The folder every local-disk configuration's relative `rootPath` goes under (`data/storage`; `/data/storage` in the image) |
| `Storage:AllowAbsolutePaths` | Whether a local-disk configuration may name an absolute folder (off; saving one is refused with 400 unless this is on) |
| `Storage:AllowPrivateNetworkHosts` | Whether an S3 `serviceUrl` (MinIO, R2) may be on a private or loopback network (off). **Independent of** `Email:AllowPrivateNetworkHosts`. Link-local and metadata addresses are never allowed |
| `Email:AllowPrivateNetworkHosts` | The same switch for SMTP hosts (off) |
| `Chat:TelegramBaseUrl` | The Telegram Bot API address (a host setting on purpose: the bot token is in the request path) |
| `Jobs:RetryDelaysSeconds` | The waits between attempts (default 1, 5, 15). The number of attempts is one more than the number of waits |
| `Jobs:WorkersPerQueue`, `Jobs:LogRetentionDays`, `Jobs:FinishedJobRetentionDays`, `Jobs:MetricsCacheSeconds`, `Jobs:SchedulesEnabled` | Workers (2), retention (90 and 7 days), how long a metrics snapshot is reused (60 s), the timer of the scheduled tasks |
| `Webhooks:AllowPrivateNetworkHosts` | Whether an outbound webhook may go to a private or loopback address (off; **switch on where the applications are on the same private network**). Link-local and metadata addresses are never allowed. Independent of the email and storage switches |
| `Webhooks:Inbound:{telegram,github,stripe}:Secret` | The secret of each inbound source. Without it the source refuses every call |
| `Authentication:Authority` | Optional: the Identity STS, to accept bearer tokens as well as API keys |
| `SharedServices:Api:BaseUrl`, `SharedServices:Api:ApiKey` | (Web) where the API is and the key the portal uses |

## How the important rules are met

- **Secrets are never stored or shown in plain text.** A secret is protected with Data Protection before it is stored
  (`encrypted_secret`). DTOs have no property that can hold it; they carry `hasSecret` and the mask `••••••••`. Settings that look like
  a secret (a `password` or `token` property, a bot token, a PEM key, a Discord or Slack webhook address) are refused.
  The audit log's details are cleaned again on the way out.
- **A stored secret goes only where it was saved for.** Changing the provider needs the new secret; so does changing an SMTP host or user name.
- **Configurations are versioned.** Saving makes the next Draft; activating a version archives the active one and older drafts; a
  rollback goes back to the previous active version (or one you name) at once. Two people changing the same series at once cannot both win.
- **Files**: the type is read from the bytes (not the name or Content-Type), the object key is made by the server, an image is checked
  against a pixel limit before it is decoded, and a file stays readable from the storage it was written to after the project changes provider.
- **Outbound calls** (SMTP host, S3 `serviceUrl` and its `{bucket}.{host}` name, webhooks) never go to link-local or metadata addresses.

## Known limits (see the task reports)

- The portal has **no sign-in yet**: anyone who can reach port 5031 acts as the portal's API key (a platform key): they can create and
  revoke keys, read the audit log, and read and delete files of every project. Keep it on a trusted network until single sign-on with the
  Identity STS is added.
- The containers run as root (the key-ring path is under `/root`, as the deployment spec asks).
- No rate limiting of the API (the inbound webhook endpoint is anonymous: it caps a body at 256 KB and logs refusals, but a flood is for a firewall), no virus scan of uploads.
- A quota is checked before a send and the send is a separate step, so requests that arrive together can overshoot a quota by a few messages.
- Events are delivered at least once: an application that answers slowly or fails after processing may see the same event again (same `X-AIH-Delivery`).
- A queue item is lost with the process, a job is not: it is found in the table at the next start. The metrics are computed from the database on request (and cached for `Jobs:MetricsCacheSeconds`), not by a separate metrics store.
- Not registered in the root `AppInHouse.sln`.

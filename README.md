# ArturRios.IdentityManager

Identity management API built with ASP.NET Core Web API (.NET 10), organized
with a Domain-Driven Design folder structure.

## Structure

```
docs/                                        Project documentation
src/
  Application/                               Use cases (empty scaffolding)
    Commands/
    Queries/
  Domain/                                    Domain model (empty scaffolding)
    Entities/
  Infrastructure/                            Cross-cutting / persistence (empty scaffolding)
  Presentation/
    ArturRios.IdentityManager.WebApi/        ASP.NET Core Web API (entry point)
  ArturRios.IdentityManager.sln
tests/                                       Test projects (empty scaffolding)
README.md
LICENSE
```

The `Application`, `Domain`, `Infrastructure`, and `tests` folders are empty
scaffolding directories. Add class-library and test projects to them as the
solution grows.

## Build & Test

```bash
dotnet build src/ArturRios.IdentityManager.sln
```

```bash
dotnet test src/ArturRios.IdentityManager.sln
```

## Configure

`Environments/.env` is a tracked template listing every variable the API reads. Real values live in
per-environment files that are gitignored. Create your local one before the first run:

```bash
cp src/Presentation/ArturRios.IdentityManager.WebApi/Environments/.env src/Presentation/ArturRios.IdentityManager.WebApi/Environments/.env.local
```

Then fill in `IDENTITY_MANAGER_DATA_CONNECTIONSTRING` (a PostgreSQL connection string),
`IDENTITY_MANAGER_DATA_DATABASETYPE` (`PostgreSql`), and the `IDENTITY_MANAGER_MASTER_USER_*`
values used to seed the first system administrator.

## Database

The schema is managed with EF Core migrations, applied explicitly — the API never migrates on
startup, and refuses to start when migrations are pending. Use the migration menu:

```bash
python scripts/migrations.py
```

It asks which environment file to load, then offers to list, create or apply migrations. The first
run needs the pinned EF tool:

```bash
dotnet tool restore
```

## Run

```bash
dotnet run --project src/Presentation/ArturRios.IdentityManager.WebApi
```

## License

Proprietary. See [LICENSE](LICENSE). Copyright &copy; 2026 Artur Rios. All
rights reserved.

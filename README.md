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

## Run

```bash
dotnet run --project src/Presentation/ArturRios.IdentityManager.WebApi
```

## License

Proprietary. See [LICENSE](LICENSE). Copyright &copy; 2026 Artur Rios. All
rights reserved.

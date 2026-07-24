namespace ArturRios.IdentityManager.WebApi.Tests.Support;

/// <summary>
///     xUnit collection that shares a single <see cref="PostgresFixture" /> across every functional
///     test class, so the PostgreSQL container is started once for the suite. Apply
///     <c>[Collection(nameof(FunctionalCollection))]</c> to functional test classes to join it.
/// </summary>
[CollectionDefinition(nameof(FunctionalCollection))]
public sealed class FunctionalCollection : ICollectionFixture<PostgresFixture>;

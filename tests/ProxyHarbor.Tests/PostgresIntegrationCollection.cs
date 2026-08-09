namespace ProxyHarbor.Tests;

/// <summary>
/// Все PostgreSQL integration-тесты используют одну внешнюю БД и поэтому не запускают
/// собственные миграции и очистку параллельно друг с другом.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresIntegrationGroup
{
    public const string Name = "PostgresIntegration";
}

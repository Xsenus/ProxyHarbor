using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class BackupSchemaInventoryTests
{
    [Fact]
    public void EveryRelationalTableHasExactlyOneExplicitBackupClassification()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseNpgsql("Host=localhost;Database=backup_schema_inventory;Username=inventory;Password=not-used")
            .Options;
        using var db = new ProxyHarborDbContext(options);

        var modelTables = db.Model.GetEntityTypes()
            .Select(entity => entity.GetTableName())
            .Where(table => table is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        var duplicateClassifications = BackupSchemaInventory.Tables
            .GroupBy(item => item.TableName, StringComparer.Ordinal)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var classifiedTables = BackupSchemaInventory.Tables
            .Select(item => item.TableName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(duplicateClassifications);
        Assert.Equal(
            modelTables.Order(StringComparer.Ordinal),
            classifiedTables.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void IncludedTablesHaveUniqueSafeDatabaseEntriesAndEphemeralTablesDoNot()
    {
        var included = BackupSchemaInventory.Tables
            .Where(item => item.Disposition == BackupTableDisposition.Included)
            .ToArray();
        var ephemeral = BackupSchemaInventory.Tables
            .Where(item => item.Disposition == BackupTableDisposition.Ephemeral)
            .ToArray();

        Assert.All(included, item =>
        {
            Assert.StartsWith("database/", item.ArchiveEntry, StringComparison.Ordinal);
            Assert.EndsWith(".json", item.ArchiveEntry, StringComparison.Ordinal);
            Assert.DoesNotContain("..", item.ArchiveEntry, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(item.Rationale));
        });
        Assert.Equal(
            included.Length,
            included.Select(item => item.ArchiveEntry).Distinct(StringComparer.Ordinal).Count());
        Assert.All(ephemeral, item =>
        {
            Assert.Null(item.ArchiveEntry);
            Assert.False(string.IsNullOrWhiteSpace(item.Rationale));
        });
        Assert.Equal(["ProxyValidationLeases"], ephemeral.Select(item => item.TableName));
    }
}

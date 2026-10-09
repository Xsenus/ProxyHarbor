using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace ProxyHarbor.Infrastructure;

/// <summary>Preserves every published settings variant in the same transaction as its snapshot cursor.</summary>
internal static class VpnConnectionProfileStore
{
    internal static string Hash(VpnCandidate candidate) => VpnConnectionProfileIntegrity.ComputeHash(candidate);

    internal static async Task UpsertAsync(ProxyHarborDbContext db, IEnumerable<VpnImportBatch> batches, CancellationToken token)
    {
        var transaction = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("VPN profile import requires the snapshot transaction.");
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using (var create = new NpgsqlCommand("""
            CREATE TEMP TABLE vpn_profile_import (
                source_id uuid NOT NULL, profile_hash text NOT NULL, host text NOT NULL,
                port integer NOT NULL, protocol integer NOT NULL, transport text NOT NULL,
                connection_uri text NULL, clash_configuration text NULL, first_seen_at timestamptz NOT NULL, seen_at timestamptz NOT NULL
            ) ON COMMIT DROP
            """, connection, transaction))
            await create.ExecuteNonQueryAsync(token);
        await using (var writer = await connection.BeginBinaryImportAsync("""
            COPY vpn_profile_import (source_id, profile_hash, host, port, protocol, transport,
                connection_uri, clash_configuration, first_seen_at, seen_at) FROM STDIN (FORMAT BINARY)
            """, token))
        {
            foreach (var batch in batches)
            {
                foreach (var candidate in batch.Candidates)
                {
                    if (candidate.ConnectionUri is null && candidate.ClashConfiguration is null) continue;
                    await writer.StartRowAsync(token);
                    await writer.WriteAsync(batch.Source.Id, NpgsqlDbType.Uuid, token);
                    await writer.WriteAsync(Hash(candidate), NpgsqlDbType.Text, token);
                    await writer.WriteAsync(candidate.Host, NpgsqlDbType.Text, token);
                    await writer.WriteAsync(candidate.Port, NpgsqlDbType.Integer, token);
                    await writer.WriteAsync((int)candidate.Protocol, NpgsqlDbType.Integer, token);
                    await writer.WriteAsync(candidate.Transport, NpgsqlDbType.Text, token);
                    if (candidate.ConnectionUri is null) await writer.WriteNullAsync(token);
                    else await writer.WriteAsync(candidate.ConnectionUri, NpgsqlDbType.Text, token);
                    if (candidate.ClashConfiguration is null) await writer.WriteNullAsync(token);
                    else await writer.WriteAsync(candidate.ClashConfiguration, NpgsqlDbType.Text, token);
                    await writer.WriteAsync(batch.FirstObservedAt ?? batch.ObservedAt, NpgsqlDbType.TimestampTz, token);
                    await writer.WriteAsync(batch.ObservedAt, NpgsqlDbType.TimestampTz, token);
                }
            }
            await writer.CompleteAsync(token);
        }
        await using var upsert = new NpgsqlCommand("""
            INSERT INTO "VpnConnectionProfiles" ("VpnSourceId", "ProfileHash", "Host", "Port", "Protocol", "Transport",
                "ConnectionUri", "ClashConfiguration", "FirstSeenAt", "LastSeenAt")
            SELECT DISTINCT ON (source_id, profile_hash) source_id, profile_hash, host, port, protocol, transport,
                connection_uri, clash_configuration,
                MIN(first_seen_at) OVER identity, MAX(seen_at) OVER identity
            FROM vpn_profile_import
            WINDOW identity AS (PARTITION BY source_id, profile_hash)
            ORDER BY source_id, profile_hash
            ON CONFLICT ("VpnSourceId", "ProfileHash") DO UPDATE
            SET "FirstSeenAt" = LEAST("VpnConnectionProfiles"."FirstSeenAt", EXCLUDED."FirstSeenAt"),
                "LastSeenAt" = GREATEST("VpnConnectionProfiles"."LastSeenAt", EXCLUDED."LastSeenAt")
            WHERE EXCLUDED."FirstSeenAt" < "VpnConnectionProfiles"."FirstSeenAt"
               OR EXCLUDED."LastSeenAt" > "VpnConnectionProfiles"."LastSeenAt"
            """, connection, transaction);
        await upsert.ExecuteNonQueryAsync(token);
    }
}

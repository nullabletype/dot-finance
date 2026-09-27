using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using DotFinance.Domain;

using Microsoft.Data.Sqlite;

namespace DotFinance.Storage;

internal static class VaultStateStore
{
    private const string MissingStateMessage = "The encrypted vault does not contain a supported reconciliation state.";

    internal static ReconciliationBook Read(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT state_version, adjustment_sequence, configuration, rules, current FROM book_state WHERE id = 1;";
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) != ReconciliationBook.CurrentStateVersion)
            throw new InvalidDataException(MissingStateMessage);
        int adjustmentSequence = reader.GetInt32(1);
        FundingConfigurationState configuration = Deserialize((byte[])reader[2], VaultJsonContext.Default.FundingConfigurationState);
        AllocationRulesState rules = Deserialize((byte[])reader[3], VaultJsonContext.Default.AllocationRulesState);
        ReconciliationSnapshot? current = reader.IsDBNull(4)
            ? null
            : Deserialize((byte[])reader[4], VaultJsonContext.Default.ReconciliationSnapshot);
        reader.Close();

        ReconciliationBookState state = new(ReconciliationBook.CurrentStateVersion, configuration, rules,
            ReadItems(connection, "imported_transactions", VaultJsonContext.Default.ImportedTransaction),
            ReadItems(connection, "settled_transaction_ids", VaultJsonContext.Default.String),
            ReadItems(connection, "effective_transactions", VaultJsonContext.Default.AllocatedTransaction),
            ReadItems(connection, "pending_corrections", VaultJsonContext.Default.PendingCorrection),
            ReadItems(connection, "import_conflicts", VaultJsonContext.Default.ImportConflict),
            ReadItems(connection, "funding_overrides", VaultJsonContext.Default.FundingOverride),
            ReadItems(connection, "reconciliation_history", VaultJsonContext.Default.ReconciliationSnapshot),
            ReadItems(connection, "accepted_corrections", VaultJsonContext.Default.AcceptedCorrection),
            ReadItems(connection, "used_reconciliation_ids", VaultJsonContext.Default.String),
            ReadItems(connection, "current_adjustments", VaultJsonContext.Default.Adjustment),
            ReadItems(connection, "active_transaction_ids", VaultJsonContext.Default.String),
            adjustmentSequence, current,
            ReadItems(connection, "settled_baseline", VaultJsonContext.Default.AllocatedTransaction));
        return ReconciliationBook.Restore(state);
    }

    internal static void Write(SqliteConnection connection, SqliteTransaction transaction, ReconciliationBookState state)
    {
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO book_state (id, state_version, adjustment_sequence, configuration, rules, current)
                VALUES (1, $version, $sequence, $configuration, $rules, $current)
                ON CONFLICT(id) DO UPDATE SET
                    state_version = excluded.state_version,
                    adjustment_sequence = excluded.adjustment_sequence,
                    configuration = excluded.configuration,
                    rules = excluded.rules,
                    current = excluded.current
                WHERE state_version <> excluded.state_version
                   OR adjustment_sequence <> excluded.adjustment_sequence
                   OR configuration <> excluded.configuration
                   OR rules <> excluded.rules
                   OR current IS NOT excluded.current;
                """;
            command.Parameters.AddWithValue("$version", state.Version);
            command.Parameters.AddWithValue("$sequence", state.AdjustmentSequence);
            command.Parameters.AddWithValue("$configuration", Serialize(state.Configuration, VaultJsonContext.Default.FundingConfigurationState));
            command.Parameters.AddWithValue("$rules", Serialize(state.Rules, VaultJsonContext.Default.AllocationRulesState));
            command.Parameters.AddWithValue("$current", state.Current is null
                ? DBNull.Value
                : Serialize(state.Current, VaultJsonContext.Default.ReconciliationSnapshot));
            command.ExecuteNonQuery();
        }

        Sync(connection, transaction, "imported_transactions", state.Records, value => value.Id, VaultJsonContext.Default.ImportedTransaction);
        Sync(connection, transaction, "settled_transaction_ids", state.SettledTransactionIds, value => value, VaultJsonContext.Default.String);
        Sync(connection, transaction, "effective_transactions", state.EffectiveTransactions, value => value.Transaction.Id, VaultJsonContext.Default.AllocatedTransaction);
        Sync(connection, transaction, "pending_corrections", state.PendingCorrections, value => value.TransactionId, VaultJsonContext.Default.PendingCorrection);
        Sync(connection, transaction, "import_conflicts", state.ImportConflicts, value => value.TransactionId, VaultJsonContext.Default.ImportConflict);
        Sync(connection, transaction, "funding_overrides", state.Overrides, value => value.TransactionId, VaultJsonContext.Default.FundingOverride);
        Sync(connection, transaction, "reconciliation_history", state.History, value => value.Id, VaultJsonContext.Default.ReconciliationSnapshot);
        Sync(connection, transaction, "accepted_corrections", state.AcceptedCorrections,
            value => $"{value.ReconciliationId}\u001f{value.Sequence:D10}", VaultJsonContext.Default.AcceptedCorrection);
        Sync(connection, transaction, "used_reconciliation_ids", state.UsedReconciliationIds, value => value, VaultJsonContext.Default.String);
        Sync(connection, transaction, "current_adjustments", state.CurrentAdjustments, value => value.Id, VaultJsonContext.Default.Adjustment);
        Sync(connection, transaction, "active_transaction_ids", state.ActiveTransactionIds, value => value, VaultJsonContext.Default.String);
        Sync(connection, transaction, "settled_baseline", state.SettledBaseline, value => value.Transaction.Id, VaultJsonContext.Default.AllocatedTransaction);
    }

    private static void Sync<T>(SqliteConnection connection, SqliteTransaction transaction, string table,
        IEnumerable<T> values, Func<T, string> key, JsonTypeInfo<T> typeInfo)
    {
        Dictionary<string, (int Order, byte[] Payload)> desired = values.Select((value, order) =>
                new KeyValuePair<string, (int, byte[])>(key(value), (order, Serialize(value, typeInfo))))
            .ToDictionary(StringComparer.Ordinal);
        HashSet<string> existing = [];
        using (SqliteCommand read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = $"SELECT item_key FROM {table};";
            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read()) existing.Add(reader.GetString(0));
        }

        foreach ((string itemKey, (int order, byte[] payload)) in desired)
        {
            using SqliteCommand upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = $"""
                INSERT INTO {table} (item_key, sort_index, payload) VALUES ($key, $order, $payload)
                ON CONFLICT(item_key) DO UPDATE SET sort_index = excluded.sort_index, payload = excluded.payload
                WHERE sort_index <> excluded.sort_index OR payload <> excluded.payload;
                """;
            upsert.Parameters.AddWithValue("$key", itemKey);
            upsert.Parameters.AddWithValue("$order", order);
            upsert.Parameters.AddWithValue("$payload", payload);
            upsert.ExecuteNonQuery();
        }

        foreach (string stale in existing.Except(desired.Keys, StringComparer.Ordinal))
        {
            using SqliteCommand delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {table} WHERE item_key = $key;";
            delete.Parameters.AddWithValue("$key", stale);
            delete.ExecuteNonQuery();
        }
    }

    private static ImmutableArray<T> ReadItems<T>(SqliteConnection connection, string table, JsonTypeInfo<T> typeInfo)
    {
        ImmutableArray<T>.Builder values = ImmutableArray.CreateBuilder<T>();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT payload FROM {table} ORDER BY sort_index;";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) values.Add(Deserialize((byte[])reader[0], typeInfo));
        return values.ToImmutable();
    }

    private static byte[] Serialize<T>(T value, JsonTypeInfo<T> typeInfo)
        => JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);

    private static T Deserialize<T>(byte[] value, JsonTypeInfo<T> typeInfo)
        => JsonSerializer.Deserialize(value, typeInfo) ?? throw new InvalidDataException("The encrypted reconciliation state is invalid.");
}
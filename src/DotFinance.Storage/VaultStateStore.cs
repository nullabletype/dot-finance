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
        command.CommandText = "SELECT state_version, adjustment_sequence, card_account_id, current FROM book_state WHERE id = 1;";
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) != ReconciliationBook.CurrentStateVersion)
            throw new InvalidDataException(MissingStateMessage);
        int adjustmentSequence = reader.GetInt32(1);
        string cardAccountId = reader.GetString(2);
        ReconciliationSnapshot? current = reader.IsDBNull(3)
            ? null
            : Deserialize((byte[])reader[3], VaultJsonContext.Default.ReconciliationSnapshot);
        reader.Close();

        FundingConfigurationState configuration = new(cardAccountId,
            ReadItems(connection, "funding_accounts", VaultJsonContext.Default.FundingAccount));
        AllocationRulesState rules = new(
            ReadItems(connection, "category_rules", VaultJsonContext.Default.CategoryRule),
            ReadItems(connection, "subcategory_rules", VaultJsonContext.Default.SubcategoryRule),
            ReadItems(connection, "reporting_categories", VaultJsonContext.Default.ReportingCategory));

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
                INSERT INTO book_state (id, state_version, adjustment_sequence, card_account_id, current)
                VALUES (1, $version, $sequence, $cardAccountId, $current)
                ON CONFLICT(id) DO UPDATE SET
                    state_version = excluded.state_version,
                    adjustment_sequence = excluded.adjustment_sequence,
                    card_account_id = excluded.card_account_id,
                    current = excluded.current
                WHERE state_version <> excluded.state_version
                   OR adjustment_sequence <> excluded.adjustment_sequence
                   OR card_account_id <> excluded.card_account_id
                   OR current IS NOT excluded.current;
                """;
            command.Parameters.AddWithValue("$version", state.Version);
            command.Parameters.AddWithValue("$sequence", state.AdjustmentSequence);
            command.Parameters.AddWithValue("$cardAccountId", state.Configuration.CardAccountId);
            command.Parameters.AddWithValue("$current", state.Current is null
                ? DBNull.Value
                : Serialize(state.Current, VaultJsonContext.Default.ReconciliationSnapshot));
            command.ExecuteNonQuery();
        }

        Sync(connection, transaction, "funding_accounts", state.Configuration.Accounts, value => value.Id, VaultJsonContext.Default.FundingAccount);
        Sync(connection, transaction, "category_rules", state.Rules.Categories, value => value.Category, VaultJsonContext.Default.CategoryRule);
        Sync(connection, transaction, "subcategory_rules", state.Rules.Subcategories,
            value => $"{value.Category}\u001f{value.Subcategory}", VaultJsonContext.Default.SubcategoryRule);
        Sync(connection, transaction, "reporting_categories", state.Rules.Reporting,
            value => $"{value.Category}\u001f{value.Subcategory}", VaultJsonContext.Default.ReportingCategory);
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

        UpdateRelationalColumns(connection, transaction, state);
    }

    private static void UpdateRelationalColumns(SqliteConnection connection, SqliteTransaction transaction,
        ReconciliationBookState state)
    {
        foreach (FundingAccount value in state.Configuration.Accounts)
            Update(connection, transaction, "funding_accounts", value.Id,
                "role = $one, protected_minimum_minor = $two, priority = $three",
                ("$one", (int)value.Role), ("$two", Minor(value.ProtectedMinimum)), ("$three", value.Priority));
        foreach (CategoryRule value in state.Rules.Categories)
            Update(connection, transaction, "category_rules", value.Category, "funding_source = $one",
                ("$one", (int)value.Source));
        foreach (SubcategoryRule value in state.Rules.Subcategories)
            Update(connection, transaction, "subcategory_rules", $"{value.Category}\u001f{value.Subcategory}",
                "category = $one, subcategory = $two, funding_source = $three",
                ("$one", value.Category), ("$two", value.Subcategory), ("$three", (int)value.Source));
        foreach (ReportingCategory value in state.Rules.Reporting)
            Update(connection, transaction, "reporting_categories", $"{value.Category}\u001f{value.Subcategory}",
                "category = $one, subcategory = $two, funding_source = $three",
                ("$one", value.Category), ("$two", value.Subcategory ?? (object)DBNull.Value), ("$three", (int)value.Source));
        foreach (ImportedTransaction value in state.Records)
            Update(connection, transaction, "imported_transactions", value.Id,
                "transaction_date = $one, amount_decimal = $two, currency = $three, category = $four, subcategory = $five, transaction_kind = $six",
                ("$one", value.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
                ("$two", value.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("$three", value.Currency),
                ("$four", value.Category), ("$five", value.Subcategory), ("$six", (int)value.Kind));
        foreach (AllocatedTransaction value in state.EffectiveTransactions)
            Update(connection, transaction, "effective_transactions", value.Transaction.Id,
                "funding_source = $one, cost_minor = $two, card_cost_minor = $three, eligible = $four",
                ("$one", value.Source is null ? DBNull.Value : (int)value.Source.Value), ("$two", Minor(value.CostGbp)),
                ("$three", Minor(value.CardCostGbp)), ("$four", value.Eligible ? 1 : 0));
        foreach (ReconciliationSnapshot value in state.History)
            Update(connection, transaction, "reconciliation_history", value.Id,
                "reconciliation_date = $one, locked = $two, card_paid = $three",
                ("$one", value.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
                ("$two", value.Locked ? 1 : 0), ("$three", value.CardPaid ? 1 : 0));
        foreach (AcceptedCorrection value in state.AcceptedCorrections)
            Update(connection, transaction, "accepted_corrections", $"{value.ReconciliationId}\u001f{value.Sequence:D10}",
                "reconciliation_id = $one, sequence = $two, transaction_id = $three",
                ("$one", value.ReconciliationId), ("$two", value.Sequence), ("$three", value.TransactionId));
    }

    private static void Update(SqliteConnection connection, SqliteTransaction transaction, string table,
        string key, string assignments, params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"UPDATE {table} SET {assignments} WHERE item_key = $key;";
        command.Parameters.AddWithValue("$key", key);
        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("A relational vault row could not be projected.");
    }

    private static long Minor(decimal value) => checked((long)(value * 100m));

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
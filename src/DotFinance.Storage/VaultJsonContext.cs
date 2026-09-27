using System.Text.Json.Serialization;

using DotFinance.Domain;

namespace DotFinance.Storage;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReconciliationBookState))]
[JsonSerializable(typeof(FundingConfigurationState))]
[JsonSerializable(typeof(AllocationRulesState))]
[JsonSerializable(typeof(ImportedTransaction))]
[JsonSerializable(typeof(AllocatedTransaction))]
[JsonSerializable(typeof(PendingCorrection))]
[JsonSerializable(typeof(ImportConflict))]
[JsonSerializable(typeof(FundingOverride))]
[JsonSerializable(typeof(ReconciliationSnapshot))]
[JsonSerializable(typeof(AcceptedCorrection))]
[JsonSerializable(typeof(Adjustment))]
[JsonSerializable(typeof(string))]
internal sealed partial class VaultJsonContext : JsonSerializerContext;
using System.Text.Json.Serialization;

using DotFinance.Domain;

namespace DotFinance.Storage;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReconciliationBookState))]
internal sealed partial class VaultJsonContext : JsonSerializerContext;
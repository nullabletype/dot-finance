using System.Text.Json.Serialization;

using DotFinance.Domain;

namespace DotFinance.Desktop;

internal sealed record VaultReceipt(string Mode, int SchemaVersion, string Cipher, int KdfIterations,
    string StateSha256, long EncryptedBytes, string Platform);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReconciliationBookState))]
[JsonSerializable(typeof(VaultReceipt))]
internal sealed partial class StorageScenarioJsonContext : JsonSerializerContext;
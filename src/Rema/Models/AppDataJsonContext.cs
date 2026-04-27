using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rema.Models;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RemaAppData))]
[JsonSerializable(typeof(Chat))]
[JsonSerializable(typeof(List<Chat>))]
[JsonSerializable(typeof(RemaSettings))]
[JsonSerializable(typeof(JsonDocument))]
[JsonSerializable(typeof(List<ChatMessage>))]
[JsonSerializable(typeof(List<Memory>))]
[JsonSerializable(typeof(List<CapabilityDefinition>))]
[JsonSerializable(typeof(RemaConfigurationExport))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string[]))]
internal partial class AppDataJsonContext : JsonSerializerContext;

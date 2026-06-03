using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumi.Models;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppData))]
[JsonSerializable(typeof(Chat))]
[JsonSerializable(typeof(List<Chat>))]
[JsonSerializable(typeof(UserSettings))]
[JsonSerializable(typeof(JsonDocument))]
[JsonSerializable(typeof(List<ChatMessage>))]
[JsonSerializable(typeof(List<McpServer>))]
[JsonSerializable(typeof(List<LumiSharedRepository>))]
[JsonSerializable(typeof(List<BackgroundJob>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string[]))]
internal partial class AppDataJsonContext : JsonSerializerContext;

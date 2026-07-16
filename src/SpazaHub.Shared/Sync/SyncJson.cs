using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SpazaHub.Domain.Common;

namespace SpazaHub.Shared.Sync;

/// <summary>
/// The one JSON contract used for sync payloads on both client and server. Three kinds
/// of properties are excluded generically:
/// TenantId (the server stamps tenancy from the authenticated context, never from payloads),
/// CachedQuantity (stock on hand is derived from movements and never transmitted as truth),
/// and collection navigations of child entities (children sync as their own items).
/// </summary>
public static class SyncJson
{
    private static readonly string[] ExcludedProperties = ["TenantId", "CachedQuantity"];

    public static readonly JsonSerializerOptions Options = Create();

    public static string Serialize(object entity, Type type)
        => JsonSerializer.Serialize(entity, type, Options);

    public static object? Deserialize(string json, Type type)
        => JsonSerializer.Deserialize(json, type, Options);

    private static JsonSerializerOptions Create()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(RemoveExcludedProperties);

        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            TypeInfoResolver = resolver
        };
    }

    private static void RemoveExcludedProperties(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        for (int i = typeInfo.Properties.Count - 1; i >= 0; i--)
        {
            var property = typeInfo.Properties[i];

            bool excludedByName = ExcludedProperties.Contains(
                property.Name, StringComparer.OrdinalIgnoreCase);

            if (excludedByName || IsEntityCollection(property.PropertyType))
            {
                typeInfo.Properties.RemoveAt(i);
            }
        }
    }

    private static bool IsEntityCollection(Type type)
    {
        if (!type.IsGenericType || !typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
        {
            return false;
        }

        return type.GetGenericArguments().Any(t => typeof(Entity).IsAssignableFrom(t));
    }
}

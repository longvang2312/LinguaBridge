using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppInHouse.SharedServices.ClientSdk;

public interface ISharedServicesClient
{
    Task<IReadOnlyList<ChannelConfigurationResponse>> ListConfigurationsAsync(
        string? projectCode = null, string? channelType = null, string? status = null, CancellationToken cancellationToken = default);

    Task<ChannelConfigurationResponse> GetConfigurationAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ChannelConfigurationResponse> SaveConfigurationAsync(SaveChannelConfigurationRequest request, CancellationToken cancellationToken = default);

    Task<ChannelConfigurationResponse> ActivateConfigurationAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ChannelConfigurationResponse> RollbackConfigurationAsync(RollbackConfigurationRequest request, CancellationToken cancellationToken = default);
}

public sealed class SharedServicesClient(HttpClient http) : ISharedServicesClient
{
    private const string Root = "api/v1/channel-configurations";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<IReadOnlyList<ChannelConfigurationResponse>> ListConfigurationsAsync(
        string? projectCode = null, string? channelType = null, string? status = null, CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(projectCode)) query.Add($"projectCode={Uri.EscapeDataString(projectCode)}");
        if (!string.IsNullOrWhiteSpace(channelType)) query.Add($"channelType={Uri.EscapeDataString(channelType)}");
        if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status)}");

        using var response = await http.GetAsync(query.Count == 0 ? Root : $"{Root}?{string.Join('&', query)}", cancellationToken);
        return await ReadAsync<List<ChannelConfigurationResponse>>(response, cancellationToken);
    }

    public async Task<ChannelConfigurationResponse> GetConfigurationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"{Root}/{id}", cancellationToken);
        return await ReadAsync<ChannelConfigurationResponse>(response, cancellationToken);
    }

    public async Task<ChannelConfigurationResponse> SaveConfigurationAsync(SaveChannelConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(Root, request, Json, cancellationToken);
        return await ReadAsync<ChannelConfigurationResponse>(response, cancellationToken);
    }

    public async Task<ChannelConfigurationResponse> ActivateConfigurationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"{Root}/{id}/activate", content: null, cancellationToken);
        return await ReadAsync<ChannelConfigurationResponse>(response, cancellationToken);
    }

    public async Task<ChannelConfigurationResponse> RollbackConfigurationAsync(RollbackConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"{Root}/rollback", request, Json, cancellationToken);
        return await ReadAsync<ChannelConfigurationResponse>(response, cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
                   ?? throw new SharedServicesApiException((int)response.StatusCode, "The service returned an empty answer.");

        throw await ToExceptionAsync(response, cancellationToken);
    }

    // The API answers with RFC 7807 problem details; turn them into one exception that carries the status and the field errors.
    private static async Task<SharedServicesApiException> ToExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? title = null, detail = null;
        Dictionary<string, string[]>? errors = null;
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                detail = root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (root.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object)
                    errors = e.Deserialize<Dictionary<string, string[]>>(Json);
            }
        }
        catch (JsonException)
        {
            // Not problem details (a proxy's error page, for example): fall back to the status line.
        }

        var message = detail ?? title ?? response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "The service did not accept the API key.",
            HttpStatusCode.Forbidden => "The API key is not allowed to do this.",
            _ => $"The service answered {(int)response.StatusCode} {response.ReasonPhrase}.",
        };
        return new SharedServicesApiException((int)response.StatusCode, message, errors);
    }
}

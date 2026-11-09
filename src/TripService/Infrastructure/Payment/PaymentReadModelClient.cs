using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace TripService.Infrastructure.Payment;

public sealed class PaymentReadModelOptions
{
    public const string SectionName = "PaymentReadModel";

    /// <summary>Base URL của PaymentService (http://payment-service:8080 trong compose).</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Tổng thời gian cho một lần đọc snapshot — trip API không được chậm vì read model.</summary>
    public int TimeoutMs { get; set; } = 1500;
}

/// <summary>
/// Gọi GET /payments/trip/{tripId} nội bộ. JWT của caller được forward nguyên trạng —
/// payments.read do role Rider/Driver/Admin có; driver xem trip của khách khác sẽ nhận
/// 403 → null, hành vi chấp nhận được (driver không cần lý do nghẽn thanh toán).
/// </summary>
public sealed class PaymentReadModelClient : IPaymentReadModelClient
{
    private readonly HttpClient _http;
    private readonly PaymentReadModelOptions _options;

    public PaymentReadModelClient(HttpClient http, IOptions<PaymentReadModelOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public async Task<PaymentSnapshot?> TryGetForTripAsync(Guid tripId, CancellationToken cancellationToken = default)
    {
        if (!HasBaseUrl())
            return null;

        using var timeoutCts = CreateTimeoutToken(cancellationToken);

        try
        {
            var url = $"{_options.BaseUrl.TrimEnd('/')}/payments/trip/{tripId}";
            using var response = await _http.GetAsync(url, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadFromJsonAsync<TripPaymentResponse>(timeoutCts.Token);
            if (body is null)
                return null;

            return new PaymentSnapshot(body.Id, MapStatus(body.Status), body.FailureCode, body.AttemptCount);
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// POST /payments/{trip}/{tripId}/retry — retry thật ủy quyền cho PaymentService.
    /// 200 → snapshot mới nhất của nghĩa vụ; 404 → chưa có nghĩa vụ; các mã khác và lỗi
    /// mạng → outcome thất bại kèm lý do ngắn để endpoint trả về thông điệp hữu ích.
    /// </summary>
    public async Task<PaymentRetryOutcome> RetryForTripAsync(
        Guid tripId, CancellationToken cancellationToken = default)
    {
        if (!HasBaseUrl())
            return PaymentRetryOutcome.Failed("PaymentService chưa được cấu hình (PaymentReadModel:BaseUrl).");

        using var timeoutCts = CreateTimeoutToken(cancellationToken);

        try
        {
            var url = $"{_options.BaseUrl.TrimEnd('/')}/payments/trip/{tripId}/retry";
            using var response = await _http.PostAsync(url, content: null, timeoutCts.Token);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return PaymentRetryOutcome.Failed($"Chưa có nghĩa vụ thanh toán cho trip {tripId}.");

            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadProblemDetailAsync(response, timeoutCts.Token);
                return PaymentRetryOutcome.Failed(detail
                    ?? $"PaymentService từ chối retry (HTTP {(int)response.StatusCode}).");
            }

            var body = await response.Content.ReadFromJsonAsync<TripPaymentResponse>(timeoutCts.Token);
            var snapshot = body is null
                ? null
                : new PaymentSnapshot(body.Id, MapStatus(body.Status), body.FailureCode, body.AttemptCount);

            return new PaymentRetryOutcome(true, snapshot, null);
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            return PaymentRetryOutcome.Failed("Không gọi được PaymentService để retry.");
        }
    }

    private bool HasBaseUrl() => !string.IsNullOrWhiteSpace(_options.BaseUrl);

    private CancellationTokenSource CreateTimeoutToken(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.TimeoutMs);
        return cts;
    }

    private static async Task<string?> ReadProblemDetailAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(payload))
                return null;

            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return null;

            foreach (var name in new[] { "detail", "title" })
            {
                if (doc.RootElement.TryGetProperty(name, out var value)
                    && value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
                                    or OperationCanceledException)
        {
            return null;
        }
    }

    private static bool IsTransportFailure(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or OperationCanceledException
              or NotSupportedException or System.Text.Json.JsonException;

    /// <summary>
    /// Ánh xạ trạng thái PayDB sang nhãn người dùng trên trip. Không bao giờ trả null:
    /// trạng thái lạ (PayDB thêm enum mới) phải hiện là "Unknown" chứ không được làm mất
    /// nhãn thanh toán của trip.
    /// </summary>
    internal static string MapStatus(string paymentStatus) => paymentStatus switch
    {
        "Settled" or "Authorized" => "Paid",
        "Failed" => "PaymentFailed",
        "Created" or "PendingRetry" or "Charging" => "Processing",
        _ => "Unknown"
    };

    private sealed record TripPaymentResponse(
        Guid Id,
        string Status,
        string? FailureCode,
        int AttemptCount);
}
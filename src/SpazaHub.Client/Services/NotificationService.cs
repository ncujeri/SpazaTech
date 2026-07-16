using Microsoft.JSInterop;

namespace SpazaHub.Client.Services;

/// <summary>
/// Local notifications for low-stock alerts. Uses the browser Notification API when
/// permitted; the POS page also shows the alert inline, so a denied permission only
/// loses the out-of-app ping.
/// </summary>
public class NotificationService
{
    private readonly IJSRuntime _js;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(IJSRuntime js, ILogger<NotificationService> logger)
    {
        _js = js;
        _logger = logger;
    }

    public async Task NotifyLowStockAsync(string productName, decimal quantityLeft)
    {
        try
        {
            await _js.InvokeVoidAsync(
                "spazaNotify",
                "Stock low",
                $"{productName}: {quantityLeft:0.###} left. Add it to the next cash-and-carry trip.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Notification failed; the inline alert still shows.");
        }
    }

    public async Task NotifyExpiryAsync(string productName, DateOnly expiryDate, bool isExpired)
    {
        try
        {
            string title = isExpired ? "Stock expired" : "Stock expiring soon";
            string body = isExpired
                ? $"{productName} expired on {expiryDate:dd MMM}. Check the shelf."
                : $"{productName} expires on {expiryDate:dd MMM}. Sell it first or mark it down.";
            await _js.InvokeVoidAsync("spazaNotify", title, body);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Notification failed; the inline alert still shows.");
        }
    }
}

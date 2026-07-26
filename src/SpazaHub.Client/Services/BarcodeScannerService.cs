using Microsoft.JSInterop;

namespace SpazaHub.Client.Services;

/// <summary>
/// Camera barcode scanning: native BarcodeDetector where the browser has it, vendored
/// ZXing fallback on older devices. Bluetooth scanners need none of this: they arrive
/// as keyboard-wedge input handled by the POS page itself.
/// </summary>
public class BarcodeScannerService : IAsyncDisposable
{
    // The ?v= query is a cache-buster for the ES module: bump it whenever barcode.js
    // changes so the browser fetches the new file instead of a sticky cached copy.
    private const string ModuleUrl = "./js/barcode.js?v=5";

    private readonly IJSRuntime _js;
    private IJSObjectReference? _module;
    private DotNetObjectReference<BarcodeScannerService>? _selfRef;

    /// <summary>Raised once per detected barcode while scanning.</summary>
    public event Action<string>? BarcodeDetected;

    public BarcodeScannerService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task StartAsync(string videoElementId)
    {
        _module ??= await _js.InvokeAsync<IJSObjectReference>("import", ModuleUrl);
        _selfRef ??= DotNetObjectReference.Create(this);
        await _module.InvokeVoidAsync("start", videoElementId, _selfRef);
    }

    public async Task StopAsync()
    {
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("stop");
        }
    }

    /// <summary>True when the live camera can toggle its flash (mostly phone rear cameras).</summary>
    public async Task<bool> IsTorchAvailableAsync()
    {
        if (_module is null)
        {
            return false;
        }

        return await _module.InvokeAsync<bool>("torchAvailable");
    }

    /// <summary>Turns the camera flash on or off; returns false if the camera has no torch.</summary>
    public async Task<bool> SetTorchAsync(bool on)
    {
        if (_module is null)
        {
            return false;
        }

        return await _module.InvokeAsync<bool>("setTorch", on);
    }

    /// <summary>Starts listening for Bluetooth keyboard-wedge scanner bursts.</summary>
    public async Task StartWedgeAsync()
    {
        _module ??= await _js.InvokeAsync<IJSObjectReference>("import", ModuleUrl);
        _selfRef ??= DotNetObjectReference.Create(this);
        await _module.InvokeVoidAsync("startWedge", _selfRef);
    }

    public async Task StopWedgeAsync()
    {
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("stopWedge");
        }
    }

    [JSInvokable]
    public void OnBarcode(string value)
    {
        BarcodeDetected?.Invoke(value);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("stop");
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Page is gone; nothing to stop.
            }
        }

        _selfRef?.Dispose();
        GC.SuppressFinalize(this);
    }
}

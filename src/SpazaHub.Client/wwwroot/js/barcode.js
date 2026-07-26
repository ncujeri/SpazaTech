// Camera barcode scanning. Prefers the native BarcodeDetector API; falls back to the
// vendored ZXing UMD bundle on browsers without it. Stops after each detection so the
// POS decides whether to keep scanning.

let stream = null;
let running = false;
let zxingReader = null;
let activeVideo = null;

const FORMATS = ["ean_13", "ean_8", "upc_a", "upc_e", "code_128", "code_39", "qr_code"];

async function loadZxing() {
  if (window.ZXing) {
    return window.ZXing;
  }
  await new Promise((resolve, reject) => {
    const script = document.createElement("script");
    script.src = "lib/zxing/zxing.min.js";
    script.onload = resolve;
    script.onerror = reject;
    document.head.appendChild(script);
  });
  return window.ZXing;
}

export async function start(videoElementId, dotnetRef) {
  const video = document.getElementById(videoElementId);
  if (!video || running) {
    return;
  }

  running = true;
  activeVideo = video;

  // Native path: fast, but BarcodeDetector only exists on Android/ChromeOS and some
  // macOS browsers. Desktop Chrome/Edge on Windows do not have it, so we fall through.
  console.log("[barcode] start; BarcodeDetector present:", "BarcodeDetector" in window);
  if ("BarcodeDetector" in window) {
    try {
      const supported = await window.BarcodeDetector.getSupportedFormats();
      const formats = FORMATS.filter(f => supported.includes(f));
      if (formats.length > 0) {
        stream = await navigator.mediaDevices.getUserMedia({
          video: { facingMode: "environment", width: { ideal: 1920 }, height: { ideal: 1080 } },
          audio: false
        });
        video.srcObject = stream;
        await video.play();

        const detector = new window.BarcodeDetector({ formats });
        const tick = async () => {
          if (!running) {
            return;
          }
          try {
            const codes = await detector.detect(video);
            if (codes.length > 0) {
              await dotnetRef.invokeMethodAsync("OnBarcode", codes[0].rawValue);
              stop();
              return;
            }
          } catch {
            // Frame not ready yet; keep polling.
          }
          requestAnimationFrame(tick);
        };
        requestAnimationFrame(tick);
        return;
      }
    } catch (err) {
      console.warn("BarcodeDetector unavailable, using ZXing fallback", err);
    }
  }

  // Fallback for everything else (incl. desktop Chrome/Edge on Windows). Let ZXing own
  // the camera: it runs getUserMedia, attaches the stream, and decodes continuously.
  // Do NOT pre-open the stream here — decodeFromVideoElement on an already-playing
  // video silently never starts, which looks like "camera opens but nothing happens".
  try {
    const ZXing = await loadZxing();
    console.log("[barcode] ZXing loaded, starting continuous decode");

    // TRY_HARDER lets ZXing retry rotated/skewed frames — matters a lot with a laptop
    // webcam and hand-held boxes where the barcode is rarely perfectly horizontal.
    const hints = new Map();
    hints.set(ZXing.DecodeHintType.TRY_HARDER, true);
    hints.set(ZXing.DecodeHintType.POSSIBLE_FORMATS, [
      ZXing.BarcodeFormat.EAN_13, ZXing.BarcodeFormat.EAN_8,
      ZXing.BarcodeFormat.UPC_A, ZXing.BarcodeFormat.UPC_E,
      ZXing.BarcodeFormat.CODE_128, ZXing.BarcodeFormat.CODE_39,
      ZXing.BarcodeFormat.QR_CODE
    ]);

    zxingReader = new ZXing.BrowserMultiFormatReader(hints);
    await zxingReader.decodeFromConstraints(
      { video: { facingMode: "environment", width: { ideal: 1920 }, height: { ideal: 1080 } }, audio: false },
      video,
      (result) => {
        if (result && running) {
          console.log("[barcode] detected:", result.getText());
          dotnetRef.invokeMethodAsync("OnBarcode", result.getText());
          stop();
        }
        // A "not found" result fires on every frame with no readable barcode; that is
        // the normal searching state, so we simply wait for the next frame.
      }
    );
  } catch (err) {
    console.error("[barcode] ZXing fallback failed to start:", err);
    running = false;
  }
}

// Bluetooth scanners act as keyboards: a fast burst of characters ending in Enter.
// Buffer keystrokes arriving within 50ms of each other and treat an Enter-terminated
// burst of 4 or more characters as a scan, so normal typing never triggers it.
let wedgeHandler = null;

export function startWedge(dotnetRef) {
  stopWedge();
  let buffer = "";
  let lastKeyTime = 0;

  wedgeHandler = (e) => {
    const now = Date.now();
    if (now - lastKeyTime > 50) {
      buffer = "";
    }
    lastKeyTime = now;

    if (e.key === "Enter") {
      if (buffer.length >= 4) {
        dotnetRef.invokeMethodAsync("OnBarcode", buffer);
        e.preventDefault();
      }
      buffer = "";
    } else if (e.key.length === 1) {
      buffer += e.key;
    }
  };
  document.addEventListener("keydown", wedgeHandler);
}

export function stopWedge() {
  if (wedgeHandler) {
    document.removeEventListener("keydown", wedgeHandler);
    wedgeHandler = null;
  }
}

// The camera's flash. Only rear cameras that expose the "torch" capability (mostly
// phones) can do this; on everything else these are safe no-ops so the UI can just
// hide the button. The active video track is whoever opened the camera — our own
// stream on the native path, or the one ZXing attached to the video element.
function currentTrack() {
  const source = stream || (activeVideo && activeVideo.srcObject);
  if (source && typeof source.getVideoTracks === "function") {
    return source.getVideoTracks()[0] || null;
  }
  return null;
}

export function torchAvailable() {
  const track = currentTrack();
  if (!track || typeof track.getCapabilities !== "function") {
    return false;
  }
  try {
    return track.getCapabilities().torch === true;
  } catch {
    return false;
  }
}

export async function setTorch(on) {
  const track = currentTrack();
  if (!track || typeof track.applyConstraints !== "function") {
    return false;
  }
  try {
    await track.applyConstraints({ advanced: [{ torch: on }] });
    return true;
  } catch (err) {
    console.warn("[barcode] torch not supported on this camera", err);
    return false;
  }
}

export function stop() {
  running = false;
  if (zxingReader) {
    try { zxingReader.reset(); } catch { /* already stopped */ }
    zxingReader = null;
  }
  if (stream) {
    stream.getTracks().forEach(t => t.stop());
    stream = null;
  }
  activeVideo = null;
}

// Camera barcode scanning. Prefers the native BarcodeDetector API; falls back to the
// vendored ZXing UMD bundle on browsers without it. Stops after each detection so the
// POS decides whether to keep scanning.

let stream = null;
let running = false;
let zxingReader = null;

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
  stream = await navigator.mediaDevices.getUserMedia({
    video: { facingMode: "environment" },
    audio: false
  });
  video.srcObject = stream;
  await video.play();

  if ("BarcodeDetector" in window) {
    const supported = await window.BarcodeDetector.getSupportedFormats();
    const formats = FORMATS.filter(f => supported.includes(f));
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

  // Older devices: ZXing fallback.
  const ZXing = await loadZxing();
  zxingReader = new ZXing.BrowserMultiFormatReader();
  zxingReader.decodeFromVideoElement(video, (result) => {
    if (result && running) {
      dotnetRef.invokeMethodAsync("OnBarcode", result.getText());
      stop();
    }
  });
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
}

// Origin Private File System persistence for the local SQLite database file.
// The .NET side reads and writes the database in the in-memory emscripten file
// system; these helpers copy the bytes to and from OPFS so data survives reloads.

export function isAvailable() {
  return !!(navigator.storage && navigator.storage.getDirectory);
}

export async function save(fileName, bytes) {
  const root = await navigator.storage.getDirectory();
  const handle = await root.getFileHandle(fileName, { create: true });
  const writable = await handle.createWritable();
  await writable.write(bytes);
  await writable.close();
  return true;
}

export async function load(fileName) {
  try {
    const root = await navigator.storage.getDirectory();
    const handle = await root.getFileHandle(fileName);
    const file = await handle.getFile();
    return new Uint8Array(await file.arrayBuffer());
  } catch {
    return null;
  }
}

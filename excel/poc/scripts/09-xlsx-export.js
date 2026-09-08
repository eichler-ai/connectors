// Whole-document export via the File API (Office.FileType.Compressed = .xlsx bytes). Run with -out book.xlsx.
const type = Office.FileType.Compressed;
const file = await new Promise((res, rej) =>
  Office.context.document.getFileAsync(type, { sliceSize: 4 * 1024 * 1024 }, r =>
    r.status === Office.AsyncResultStatus.Succeeded ? res(r.value) : rej(new Error(r.error.code + ": " + r.error.message))));
try {
  const chunks = [];
  let total = 0;
  for (let i = 0; i < file.sliceCount; i++) {
    const slice = await new Promise((res, rej) => file.getSliceAsync(i, r =>
      r.status === Office.AsyncResultStatus.Succeeded ? res(r.value) : rej(new Error(r.error.code + ": " + r.error.message))));
    const bytes = slice.data instanceof Uint8Array ? slice.data : new Uint8Array(slice.data);
    chunks.push(bytes); total += bytes.length;
  }
  const all = new Uint8Array(total); let off = 0;
  for (const c of chunks) { all.set(c, off); off += c.length; }
  let bin = ""; for (let i = 0; i < all.length; i += 0x8000) bin += String.fromCharCode.apply(null, all.subarray(i, i + 0x8000));
  return { base64: btoa(bin), size: file.size, slices: file.sliceCount, dataType: Object.prototype.toString.call(chunks[0]) };
} finally {
  await new Promise(res => file.closeAsync(res));
}

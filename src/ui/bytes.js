// Dosya baytları arayüzde bstr olarak tutulur (her karakter 0-255): IndexedDB/JSON oturumuna olduğu gibi yazılır,
// çözmeye Uint8Array olarak verilir; böylece CRLF, BOM ve geçersiz UTF-8 editör yüzünden bozulmaz.
export { fromBytes, toBytes, toText } from '../core/bstr.js';

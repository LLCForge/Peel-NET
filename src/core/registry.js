// Geçiş sırası (motorla aynı: engine/Passes.cs Order). Önce korumalar ve akış, sonra çağrı/dizge çözümü, en son adlar.
export const PASSES = [
  { id: 'antidebug', title: { tr: 'Anti-debug kesme', en: 'Anti-debug removal' } },
  { id: 'resources', title: { tr: 'Şifreli kaynaklar', en: 'Encrypted resources' } },
  { id: 'controlflow', title: { tr: 'Akış düzleştirme', en: 'Control-flow unflattening' } },
  { id: 'proxy', title: { tr: 'Vekil çağrılar', en: 'Proxy calls' } },
  { id: 'strings', title: { tr: 'Dizge ve sabit çözücüler', en: 'String & constant decryptors' } },
  { id: 'fold', title: { tr: 'Sabit katlama', en: 'Constant folding' } },
  { id: 'rename', title: { tr: 'Yeniden adlandırma', en: 'Renaming' } },
  { id: 'cleanup', title: { tr: 'İşaret temizliği', en: 'Marker cleanup' } },
];

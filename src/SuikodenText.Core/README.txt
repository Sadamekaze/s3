SuikodenText - Relokasi teks menu di ETC

Ganti empat file .cs lengkap pada SuikodenText.Core dengan file dalam paket ini.
Gunakan model proyek dari patch sebelumnya yang sudah memiliki Strings/Executable.

Perubahan:
- Export teks UI bawaan EXE/ELF aktif kembali, tetapi hanya dari tabel pointer
  yang sudah diverifikasi; tidak ada scan string mentah atau `exe_call_*`.
- Menambah tabel descriptor item/menu ELF `0x01993948`: 156 record berukuran
  `0x44`, dengan dua slot pointer teks per record. Jalur runtime sudah
  dikonfirmasi: loader descriptor → field objek `+0x100` → `0x0169CC68` →
  `0x01661F50`. Termasuk nama/deskripsi seperti `Feather Band`.
- Menambah katalog tabel pointer UI utama. Contohnya `0x0195FF68` (Browse
  skill points), `0x0195FF6C` (Browse profile), dan `0x0196002C/30`
  (pesan error equip/exchange). Teks panjang memakai copy-on-write ELF dan
  slot pointer tabelnya diperbarui.
- Menambah dua tabel database gameplay yang berstruktur: 28 record item
  (`0x019A0F00`, stride `0x10`) dan 37 record skill (`0x019A9100`,
  stride `0x1C`), masing-masing pointer nama dan deskripsi.
- Validator format menganggap `%d`, `%s`, `%04d`, dan `%%` sebagai token,
  tetapi tidak lagi salah membaca teks biasa seperti `10% chance`.
- Memperluas empat profil modul lokasi ETC dengan katalog nama area yang
  direferensikan pointer, termasuk `Ancient Highway` dan
  `Plain of Amur-North`. Teks kosong pada tabel pointer tidak diexport.
- Teks ELF yang lebih pendek ditulis di slot asal. Teks yang lebih panjang atau
  berbagi slot memakai copy-on-write ke celah nol file-backed PT_LOAD yang
  diverifikasi. Jika celah penuh, dibuat PT_LOAD kedua khusus teks terjemahan
  setelah akhir memori segmen asli; slot pointer katalog diperbarui.
- Teks menu ETC yang lebih panjang disimpan di modul ETC pemiliknya.
- Tujuan relokasi berada setelah ukuran file modul DAN ukuran runtime saat ini,
  sehingga tidak memakai area kode, teks lama, atau data kerja/BSS.
- Pointer, ukuran alokasi modul, dan descriptor sektor archive diperbarui.
- ELF tetap diperlukan karena metadata pemuatan ETC berada di ELF. Teks yang
  sumbernya dari ETC tidak ditaruh di ELF; hanya teks UI native ELF yang dapat
  direlokasi di ELF.
- ID mengikuti slot referensi; hasil import dapat diexport dan diedit lagi.
- Referensi ETC yang sudah menunjuk teks di ELF disalin kembali ke modul ETC
  saat import, termasuk teks yang tidak diedit. Byte lama dalam ELF dibiarkan
  karena tidak boleh diasumsikan tidak lagi dipakai oleh referensi lain.
- Saat teks berbagi alamat, hanya slot yang diedit yang dialihkan ke salinan baru.

Cara pakai:
1. Pilih pasangan ETC dan ELF dari hasil import yang sama.
2. Export JSON baru.
3. Edit translation, pertahankan token RAW dan format printf.
4. Import, lalu gunakan KEDUA file hasilnya: ETC dan ELF.
5. Untuk mengedit lagi, export dari pasangan hasil import terakhir.

Batasan:
- Berlaku untuk profil modul ETC versi US yang sudah dikenali.
- Pertumbuhan dibatasi kapasitas arena; bukan panjang tanpa batas.
- Relokasi menu panjang pada archive FSECT belum didukung.
- Bank rodata yang belum memiliki tabel pointer/call-site terverifikasi belum
  diexport; string tidak akan dimasukkan hanya karena muncul di `.rodata`.
- Relokasi berulang menambah tempat baru; teks lama belum dikompaksi.
- Segmen ELF tambahan diperiksa secara binary (header dan batas alamat), tetapi
  tetap perlu diuji di PCSX2 sebelum dipakai pada patch rilis.
- Lebar kotak teks/line wrapping di game terpisah dari kapasitas penyimpanan.
- Belum diuji di PCSX2. Pemeriksaan binary tidak membuktikan seluruh perilaku
  alokasi runtime dan tampilan di emulator.

Dasar disassembly SLUS yang dipakai:
0x01732670: descriptorPointers[moduleId].
0x01732920: runtimeSizes[moduleId].
0x01730870: pembulatan (size + 0x800) & ~0x7FF.
0x01732EA8: alokasi modul berdasarkan ukuran tersebut, lalu pemuatan modul.
0x017309A8: mengambil descriptor dan meneruskan tujuan alokasi.
0x0188B758 / 0x0188B788: tabel alamat awal dan ukuran arena.
Alamat tabel dibaca dari instruksi yang diverifikasi, dan referensi descriptor
harus cocok dengan record ETC yang sedang diproses.

Hasil pengujian pada sesi sebelumnya:
- Build C# berhasil.
- 347 entri menu ETC; 0 entri teks bawaan executable pada export.
- Import tanpa edit: ETC dan ELF identik.
- Tiga putaran, masing-masing memperpanjang 31 teks lagi: pointer dan isi teks
  terverifikasi; blok yang tidak diedit identik.
- Perubahan ELF hanya pada descriptor archive dan ukuran alokasi modul.
- Import tanpa edit setelah putaran ketiga: identik.
- Fixture pointer ETC ke ELF berhasil dikembalikan ke ETC.
- Mengedit satu referensi yang berbagi target tidak mengubah referensi lain.

Salinan kerja sempat dibersihkan selama jeda percakapan. Paket ini dipulihkan
dari source versi 4 dan perubahan yang tercatat dalam sesi pengujian tersebut.
Pengujian binary tidak dijalankan ulang setelah pemulihan.

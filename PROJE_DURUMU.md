# SpsWebBI - Geliştirme Durumu ve Mimari Hafıza

**Son Güncelleme:** 2026-11-06
**Proje Yolu:** `c:\Projeler\SpsWebBI`

---

## Tamamlanan Adımlar

### [x] Adım 1: Proje İskeleti, Siber Güvenlik Katmanı ve MariaDB Şeması
- `SpsWebBI.csproj` (.NET 8.0 Web Host App, `Dapper`, `MySqlConnector`).
- `Security/PasswordHasher.cs`: PBKDF2 (HMAC-SHA256, 100.000 iterasyon, 16-byte Salt, Timing-Attack korumalı doğrulama).
- `Security/ConnectionProtector.cs`: Bağlantı dizesindeki parolayı Windows DPAPI (`ENC:...`) ile şifreleme ve `AllowBatch=false`, `AllowLoadLocalInfile=false` sürücü güvenliği.
- `Security/SqlSecurityValidator.cs`: Dinamik SQL sorgularında yalnızca `SELECT` / `WITH` izni, çoklu komut (`;`) engeli, DDL/DML/Sistem komutu engeli ve `@Parametre` otomatik çıkarımı.
- `Data/DatabaseInitializer.cs`: MariaDB üzerinde `bi_users`, `bi_datasources`, `bi_datasource_params`, `bi_dashboards`, `bi_user_dashboards`, `bi_audit_logs` tablolarının otomatik kurulumu.

### [x] Adım 2: Kimlik Doğrulama Servisi ve 5 Açılış Senaryosu (`User`, `Security`, `Mode`)
- `Services/DbConnectionManager.cs` & `Controllers/DbSettingsController.cs` & `wwwroot/db-settings.html`: Program içinden MariaDB bağlantı bilgilerini (`127.0.0.1:3323`, `spsteks`) test etme ve DPAPI ile şifreli kaydetme ekranı.
- `Services/AuthService.cs`: PBKDF2 şifre/PIN doğrulama, 5 hatalı girişte 15 dakika Brute-Force kilitleme, `Security=0` (`allow_passwordless`) yetki doğrulaması ve `bi_audit_logs` güvenlik kaydı.
- `Controllers/AuthController.cs` & `wwwroot/login.html`: Madde 7 ve Madde 8 (8.1 TV Slayt, 8.2/8.3 `Security=0` doğrudan kontrol ekranı, 8.4 `Security=1` kullanıcı sabitli PIN/Şifre ekranı, 8.5 parametresiz tam Login ekranı) kurallarının tamamı.
- `Security/UserTokenProtector.cs`: `Security=0` için AES-256 + HMAC-SHA256 şifreli/imzalı `User` token doğrulaması (düz metin kullanıcı adıyla şifresiz giriş engeli) + tarayıcı adres çubuğundan parametrelerin anında silinmesi (`history.replaceState`).

### [x] Adım 3: Dinamik Veri Kaynağı (Datasource), Parametre ve Güvenli SQL Motoru (Madde 2.4, 2.5, 4, 5, 9)
- `Services/DynamicQueryService.cs`: Sıfır C# DTO sınıfı ile çalışan dinamik SQL motoru, otomatik kolon veri tipi (`number`, `date`, `string`, `boolean`) çıkarımı, `@Parametre` filtre bağlama ve `lookup_sql` ile dinamik açılır liste (LookUp) doldurma.
- `Controllers/DataSourceController.cs`: Kullanıcının yetkili olduğu dashboard/datasource listesi, parametre çekme, güvenli sorgu çalıştırma, SQL önizleme ve yeni veri kaynağı kaydetme API uçları.
- `Data/DatabaseInitializer.cs`: `spsteks` veritabanı için başlangıç **Satış & Sipariş** (`DS_SIPARIS`) ve **Depo & Stok Durum** (`DS_STOK`) veri kaynaklarının, filtre parametrelerinin ve hazır dashboard layout'larının otomatik oluşturulması.

### [x] Adım 4 ve Adım 5: Gösterim Kontrol Paneli, Slayt/Otomatik Yenileme Motoru, Görsel BI Bileşenleri ve Kişisel Layout Kaydı
- `wwwroot/lib/echarts.min.js`: Kapalı devre yerel ağda (offline) çalışması için Apache ECharts 5 yerel olarak projeye eklendi.
- `Controllers/DashboardController.cs`: Son kullanıcının kendi yaptığı tasarım özelleştirmelerini `bi_user_dashboards.custom_layout_json` alanına kaydetmesi (`POST /api/dashboards/{id}/user-layout`), sıfırlaması (`DELETE`) ve Admin ana şablon kaydı (`POST /api/dashboards/save-master`).
- `wwwroot/app.html`:
  - Datasource/Dashboard seçimi, dinamik filtreler, birbirini dışlayan **"Slayt Görünümü"** ve **"Otomatik Yenileme"** checkbox'ları ve **"Göster"** butonu (Madde 7.3 & 8).
  - KPI, Sütun, Çizgi, Pasta, Halka, Veri Tablosu ve Pivot Tablo bileşenleri (Madde 2.1).
  - **Tek Tık (Cross-Filter - Madde 2.2):** Tıklanan değere göre tüm diğer grafik/tablo/KPI'ların anlık filtrelenmesi.
  - **Çift Tık (Drill-Down & Drill-Through - Madde 2.3):** Alt kırılıma inme (Breadcrumb ile geri dönme) ve en alt kırılımda çift tıklanan kaydın detay satırlarını açma.
  - **Son Kullanıcı Tasarım Özelleştirme (Madde 10):** Kullanıcının kendi ekranında bileşen ekleyip/düzenleyip kendi hesabına kaydedebilmesi.

---

### [x] Adım 6 & Adım 7: Ayrı Dashboard Tasarım Stüdyosu (`/designer`) ve Yetki / Öncelik / Süre Yönetim Ekranı (`/admin`)
- `wwwroot/designer.html`:
  - **Sekme 1 (Veri Kaynağı & SQL Editörü):** C# DTO sınıfı gerektirmeden (`Madde 2.5`) parametreli `SELECT` sorgusu yazma, `@Parametre`'leri sorgudan otomatik yakalama, veri tipi ve LookUp SQL tanımlama, canlı önizleme ve `bi_datasources` tablosuna kaydetme.
  - **Sekme 2 (Ana Dashboard Şablon Tasarımcısı):** Veri kaynağının kolonlarını otomatik çekerek KPI, Grafik, Tablo, Pivot bileşenleri ve Alt Kırılım (Drill-Down) alanlarını görsel olarak tasarlama ve `bi_dashboards.layout_json` alanına kaydetme (`Madde 8.6 & 9`).
- `Controllers/AdminController.cs` & `wwwroot/admin.html`:
  - **Kullanıcı & Güvenli Link (Token) Yönetimi:** Kullanıcı oluşturma/düzenleme, `allow_passwordless` (`Security=0`) izni verme ve `UserTokenProtector` ile şifreli TV/Exe/Tarayıcı bağlantı linklerini (`?User=UT1_...&Security=0`) tek tıkla üretip kopyalama.
  - **Yetki, Öncelik ve Süre Matrisi (`Madde 6`):** Kullanıcı bazında hangi dashboard'ları görebileceğini (`can_view`), slayt gösterim önceliğini (`display_order`), tekil otomatik yenileme süresini (`refresh_seconds`) ve slayt geçiş süresini (`slide_duration_seconds`) yönetme ve kaydetme.
  - **Güvenlik Denetim Kayıtları (`bi_audit_logs`):** Giriş denemeleri, yetkisiz erişimler ve sistem işlemlerinin izlenmesi.

---

### [x] Adım 8: Canlı MariaDB (`spsteks`) Bağlantı/Şema Doğrulaması, Güvenlik Testleri ve Uygulamanın Başlatılması
- `127.0.0.1:3323` (`spsteks`) MariaDB veritabanı şifresi `appsettings.json` içinde otomatik olarak Windows DPAPI (`ENC:...`) ile şifrelendi.
- `bi_users`, `bi_datasources`, `bi_datasource_params`, `bi_dashboards`, `bi_user_dashboards`, `bi_audit_logs` tabloları canlı veritabanında oluşturuldu.
- `DS_SIPARIS` (18 kolon, 290 satır) ve `DS_STOK` (10 kolon, 1127 satır) veri kaynakları sıfır C# DTO ile canlı `spsteks` veritabanından başarıyla çekildi.
- Düz metin `?User=A&Security=0` sızma testi engellendi, AES-256 + HMAC-SHA256 imzalı `UT1_...` token ile `8.1`, `8.2`, `8.3`, `8.4`, `8.5` senaryoları uçtan uca doğrulandı.
- Uygulama `http://localhost:5180` adresinde canlı olarak çalışmaktadır.

---

## Sıradaki Adım (Devam Eden Çalışma - 2026-11-06)

### [x] Adım 9: `designer.html` (ve `app.html`) Tasarım Stüdyosu Ekran Yerleşiminin Yenilenmesi (%100 / %125 Ölçek, Alfabetik Kolonlar ve 2D Yoğun Grid)
- **Çözülen Sorunlar:**
  1. `wwwroot/designer.html` yan yana 2 tam boy bağımsız dikey panel (`2. Seçili Bileşen & Veri Yuvaları` ve `3. Veri Kaynağı Kolonları`) + çift modlu bileşen paleti (`Sol Dikey Ray` / `Üst Yatay Şerit`) + alt durum çubuğu (`Status Bar`) mimarisine geçirildi; eski tasarım kaldırıldı.
  2. Veri kaynağı kolonları ve veri yuvası seçim kutuları Türkçe (`tr-TR`) alfabetik (`A → Z`) sıralı hale getirildi.
  3. Tuval ızgarası (`designer.html` ve `app.html`) 2 boyutlu yoğun mikro-grid (`grid-auto-rows: 6px; grid-auto-flow: row dense;` ve `grid-row: span ...`) yapısına geçirildi; kısa KPI kartlarının altında kalan boşluklara diğer bileşenlerin otomatik veya sürükle-bırak ile yerleşmesi sağlandı.

---

### [x] Adım 10: Sayısal/Tarih Formatlama Motoru, Excel Tarzı Hizalama İkonları, 2B Serbest Sürükleme ve Yuva Temizleme Senkronizasyonu (2026-11-06)
- **Sayısal ve Tarih Alanı Formatlama (`columnFormats`):**
  - Sayısal (`number`) kolonlar varsayılan olarak **formatsız/ham** (`2026`, `10021`) gösterilir; istenilen kolonlara `#,##0`, `#,##0.00`, `0.00`, `₺#,##0.00`, `%0.0` vb. özel format maskeleri hem satır içi `Biçim:` kutusundan hem de `#colFormatModal` penceresinden tanımlanabilir.
  - Tarih (`date`) kolonları varsayılan `dd.MM.yyyy` formatında gösterilir; istenirse `dd.MM.yy`, `yyyy-MM-dd`, `MM.yyyy`, `dd.MM.yyyy HH:mm` gibi özel maskeler verilebilir.
- **Excel Tarzı Hizalama İkonları (`columnAlignments` & `w.align`):**
  - Hem bileşen bazında hem de kolon bazında (`Değer / Ölçüm`, `Boyut & Kırılım`, `Tablo Sütunları`, `3. Kolonlar A-Z`) Excel tarzı **Sola Yasla / Ortala / Sağa Yasla** SVG ikon butonları eklendi.
  - Tablolarda hizalama yalnızca veri hücrelerine (`<tbody> <td>`) ve alt toplamlara (`<tfoot> <td>`) uygulanır; tablo başlıkları (`<thead> <th>`) sabit kalır.
- **Tüm Bileşenlerde Yuva Temizleme ve Kolon Listesi (`✓`) Senkronizasyonu:**
  - Tablo, Grafik, KPI, İbre ve Pivot nesnelerinde `Temizle` ve `✕` butonları ile kolonlar sıfırlandığında otomatik kolon ataması engellendi ve `3. Kolonlar (A-Z)` listesindeki `✓` işaretlerinin anında güncellenmesi sağlandı.




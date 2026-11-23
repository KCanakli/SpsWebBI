# SpsWebBI Projesi Kuralları ve Kalıcı Hafıza

Bu dosya, `c:\Projeler\SpsWebBI` projesinde alınan tüm mimari, siber güvenlik ve iş kurallarını yeni oturumlarda eksiksiz hatırlamak için oluşturulmuştur.

## 1. Çalışma Prensibi ve Token Tasarrufu (Zorunlu)
- **Tek Turda Doğrudan Uygulama:** Kullanıcının isteği net ise onay turu beklemeden doğrudan uygulanır ve yapılan işlem kısaca Türkçe raporlanır. Yalnızca belirsiz durumlarda veya kritik/mimari değişikliklerde önce kullanıcıya sorularak onay alınır.
- **İngilizce Prompt -> Türkçe Yanıt:** Kullanıcı token tasarrufu amacıyla isteklerini (promptlarını) **İngilizce** yazsa dahi, tüm açıklamalar, sorular, kod içi kullanıcı mesajları ve raporlamalar **Türkçe** olarak yapılır.
- **Toplu Dokümantasyon:** `PROJE_DURUMU.md` dosyası her küçük adımda değil, yalnızca kullanıcı istediğinde veya Git/GitHub güncellemesi öncesinde topluca güncellenir.

## 2. Mimari ve Veritabanı Özeti
- **Proje Dizini:** `c:\Projeler\SpsWebBI` (.NET 8.0 Web Host App, Dapper, MySqlConnector, yerel Apache ECharts 5).
- **MariaDB Bağlantısı:** `127.0.0.1`, Port: `3323`, Veritabanı: `spsteks`, Kullanıcı: `root` (Şifre `appsettings.json` içinde Windows DPAPI `ENC:...` ile şifrelidir; `/db-settings` ekranından değiştirilebilir).
- **Sıfır C# DTO Kuralı (Madde 2.5):** Yeni raporlar için C# sınıfı yazılmaz; `Services/DynamicQueryService.cs` SQL sorgularından kolon ve veri tiplerini dinamik çıkarır.
- **Şifreli Token Güvenliği (`Security=0`):** Düz metin `?User=A&Security=0` ile şifresiz giriş reddedilir. Şifresiz giriş yalnızca `Security/UserTokenProtector.cs` ile üretilen AES-256 + HMAC-SHA256 imzalı token (`UT1_...`) ve `allow_passwordless = 1` yetkisi ile çalışır; giriş anında adres çubuğu `history.replaceState` ile temizlenir ve gerçek `user_id` Cookie oturumuna yazılır.

## 3. Varsayılan Hesaplar ve Hazır Raporlar
- **`admin`:** Şifre `Admin123!` (Sistem Yöneticisi)
- **`A`:** Şifre/PIN `1234` (`allow_passwordless = 1`, Token: `UT1_Pk9kKv_b7FwpHEwEjPV1s6QRKxsC7mXooD0n8k2TD8Y6fCQnaFxX1J2RAKM`)
- **Hazır Dashboard'lar:** `DASH_SIPARIS` (`DS_SIPARIS` - Satış & Sipariş) ve `DASH_STOK` (`DS_STOK` - Depo & Stok Durum).

## 4. Tasarım Stüdyosu (`designer.html`) ve İzleme Ekranı (`app.html`) Grafik/Format Hafızası
- **Formatlama ve Hizalama:** `columnFormats` (sayı/tarih maskeleri) ve `columnAlignments` / `w.align` (Excel tarzı Sola/Ortaya/Sağa SVG hizalama) tüm grafik, KPI, tablo (`<tbody>`/`<tfoot>`) ve pivot hücrelerinde ortaktır.
- **Grafik Aileleri ve 39 Alt Varyant (`CHART_SUBTYPE_CATALOG`):** 18 ana bileşen tipi (`bar`, `stackedBar`, `horizontalBar`, `line`, `area`, `pie`, `donut`, `gauge`, `treemap`, `sunburst`, `sankey`, `funnel`, `radar`, `scatter`, `heatmap`, `kpi`, `pivot`, `table`) ve `w.subType` üzerinde çalışan 39 alt ECharts varyantı + `getVariantSlotPolicy(w)` akıllı veri yuvası kuralları hem `designer.html` hem de `app.html` içinde senkronize çalışır.
- **Hamburger Menü, 3 Tema (`dark`, `light`, `spsteks`) ve Çoklu Veri Kaynağı / Bağlantı Mimarisi (`designer.html`, `app.html`, `db-settings.html`):** Sol üstteki `☰` hamburger menü sırasıyla **1. Veri Kaynağı Ayarları** (`/db-settings`), **2. Veri Kaynağı (SQL Sorgu Tanımlama)** (`#sqlEditorView`), **3. Dashboard Ekle / Tanımla** (`#dashboardEditorView`) ve **4. Yetkilendirme** (`/admin`) seçeneklerini içerir; üst çubuğun sağında doğrudan **Dashboard** seçimi ve 3 seçenekli (`🌙 Koyu Tema`, `☀️ Açık Tema`, `💎 SpsTeks Mavi`) tema kutusu bulunur. `/db-settings` ekranında sınırsız sayıda aktif bağlantı (`Canlı`, `Test1`, `Test2` vb.) tanımlanabilir; `#sqlEditorView` ekranında her SQL sorgusunun hangi bağlantıda (`connection_id`) çalışacağı seçilir ve `📋 Kopyasını Oluştur` ile Canlı/Test kopyası tek tıkla üretilir. Ayrıca bir dashboard üzerindeki her grafik bileşeni ister dashboard'un varsayılan sorgusunu ister kendi özel SQL sorgusunu (`w.dataSourceId`) kullanabilir; Akıllı Cross-Filter (`getFilteredRows`) tıklanan kolon yalnızca ilgili grafiğin veri şemasında mevcutsa filtre uygular.
- **Veri Kaynakları Grid/Modal ve Viewer Dikey Panel / Alt Durum Çubuğu (Status Bar):**
  - `#sqlEditorView` içinde veri kaynakları tam boy, sıralanabilir/aranabilir grid (`#dsMasterGridTable`) üzerinde yönetilir; `✏️ Güncelle` veya çift tık ortalanmış modal (`#dsFormSection`) açar. Kaynaklar aktif/pasif (`is_active`) yapılabilir.
  - Viewer (`app.html`): Üst başlık 36px kompakttır. Sol panel (`#controlBarSection`) dikey, parametreleri alt alta barındıran, `«` ile gizlenebilen ve `#sidebarResizer` ile fareyle 180px–520px genişletilebilen yapıdadır.
  - Viewer Alt Durum Çubuğu (`#viewerStatusBar`): Ekranın en altına sabitlenmiş 26px çubuk; aktif cross-filter rozetlerini (`Kolon = Değer ✕`), anlık kayıt sayacını (`filtrelenen / toplam`) ve `Tümünü Temizle` butonunu barındırır.
- **BI Sistem Veritabanı ve İşletme Veri Kaynakları Ayrımı (`/bi-settings` vs `/db-settings`):**
  - BI sisteminin kendi tabloları (`bi_users`, `bi_dashboards`, `bi_datasources` vb.) `appsettings.bi.json` ve `/bi-settings` ekranından yönetilir. İşletme ERP/SQL veritabanı bağlantıları ise `/db-settings` ekranından yönetilir ve ikisi birbirinden izoledir.
- **Görsel Alt Stil Galerisi (`🎨 Galeri`), Apache ECharts Görsel Araçları, 39 SVG Vektör ve Son Kullanıcı Yetki/Sağ Tuş Kuralı:**
  - `designer.html` ekranında grafik kartlarının başlığında ve sağ panelde `🎨 Galeri` ile 39 alt varyant ve 9 adet Apache ECharts görsel özelliği (`123 Değerler`, `⎯ Ortalama`, `📍 Min/Maks`, `📈 Medyan`, `🔍 Zoom Kaydırıcı`, `🧮 Veri Tablosu`, `〰️ Yumuşak Çizgi`, `🧱 Kılavuz Çizgileri`, `🏷️ Gösterge`) yer alır.
  - **Son Kullanıcı Ekranı (`app.html`) Kesin Kuralı:** Normal izleme modunda son kullanıcı grafiklerde hiçbir tasarım, alt stil veya görsel özellik değişikliği yapamaz. Normal modda bir grafiğe sağ tıklandığında açılan menüde yalnızca **Veriyi Canlı Yenile**, **Grafik Görselini İndir (PNG)** ve **Veriyi Excel (CSV) İndir** (ve aktifse *Üst Kırılıma Dön* / *Aktif Filtreleri Temizle*) seçenekleri yer alır.
  - Kullanıcının ilgili dashboard için **Tasarımı Özelleştir** (`can_edit_layout = 1`) yetkisi varsa, yalnızca sol üst hamburger menüden **`🎨 Tasarımımı Özelleştir`** seçeneğini seçerek kişisel tasarım moduna (`isUserEditMode = true`) geçtikten sonra grafiklerde değişiklik yapabilir (sağ tuş menüsünde açılan **`🎨 Grafik Alt Stili & Görsel Özellikler`**, **`✏️ Kişisel Görünümü Düzenle`** ve **`🗑️ Görünümden Kaldır`** seçenekleri yalnızca bu mod aktifken görünür ve çalışır).
  - Seçilen alt varyant ve görsel özellikler hem bileşen yapısına (`currentLayout`) hem de `localStorage` (`sps_widget_subtype_${dashId}_${widgetId}` ve `sps_widget_visual_${dashId}_${widgetId}`) içine kaydedilir.

## 5. Referans Dosyalar
- Detaylı adım özeti: `c:\Projeler\SpsWebBI\PROJE_DURUMU.md`
- Tam sohbet geçmişi: `c:\Projeler\SpsWebBI\Sohbet_Gecmisi_2026-11-05.txt` ve `c:\Projeler\SpsWebBI\Sohbet_Gecmisi_2026-11-05.pdf`

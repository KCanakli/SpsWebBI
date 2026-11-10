# SpsWebBI - Geliştirme Durumu ve Mimari Hafıza

**Son Güncelleme:** 2026-11-10
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

---

### [x] Adım 11: Zengin ECharts Grafik Aileleri, 39 Alt Varyant Stili (`CHART_SUBTYPE_CATALOG`) ve Akıllı Veri Yuvası Kuralları (2026-11-07)
- **5 Yeni Ana Grafik Ailesi (`TOOLBOX_ITEMS` & `Görsel Tipi`):**
  - `sunburst` (☀️ Güneş Patlaması - Hiyerarşik Halkalar)
  - `sankey` (🔀 Sankey Akış Diyagramı - Kaynak ➔ Hedef)
  - `funnel` (🌪️ Huni / Piramit Grafiği)
  - `radar` (🕸️ Çokgen / Dairesel / Dolgulu Radar Grafiği)
  - `scatter` (🫧 Nokta Dağılım / Değer Büyüklüğüne Göre Baloncuk / Dalgalı `effectScatter`)
- **8 Grafik Ailesinde 39 Alt Grafik Varyantı (`w.subType` & `CHART_SUBTYPE_CATALOG`):**
  - **Sütun (`bar`, `stackedBar`, `horizontalBar` - 10 Stil):** `basic`, `background`, `stacked`, `stacked100` (%100 Oransal), `horizontal`, `horizontalStacked`, `waterfall` (Şelale/Kümülatif), `polarRadial` (Radyal Kutup), `polarTangential` (Teğetsel Halka), `pictorialDotted` (Kesikli Dijital).
  - **Çizgi & Alan (`line`, `area` - 7 Stil):** `smooth`, `basic`, `stackedLine`, `step` (Basamaklı), `area`, `stackedArea`, `gradientArea` (Gradyan Dolgulu & Nokta Vurgulu).
  - **Pasta & Halka (`pie`, `donut` - 6 Stil):** `pie`, `donut` (Yuvarlatılmış), `halfDonut` (Yarım Halka 180°), `roseArea` (Nightingale Alan), `roseRadius` (Nightingale Yarıçap), `nested` (İç İçe 2 Kırılımlı Pasta/Halka).
  - **İbre (`gauge` - 4 Stil):** `progress`, `classic`, `ring` (Tam Halka), `grade` (Kırmızı-Sarı-Yeşil Performans Bölgeli).
  - **Hiyerarşi & Akış (`treemap`, `sunburst`, `sankey` - 3 Stil):** `treemap`, `sunburst`, `sankey`.
  - **Huni (`funnel` - 3 Stil):** `descending`, `ascending`, `sharp`.
  - **Radar (`radar` - 3 Stil):** `polygon`, `circle`, `filled`.
  - **Dağılım (`scatter` - 3 Stil):** `scatter`, `bubble`, `effect`.
- **Varyanta Duyarlı Akıllı Veri Yuvası Politikası (`getVariantSlotPolicy`):**
  - Tekil değer gerektiren varyantlarda (`waterfall`, `polarRadial`, `polarTangential`, `pie`, `funnel`, `treemap`, `sunburst`, `sankey`, `heatmap`, `gauge`) Değer yuvası otomatik olarak 1 ölçüme sınırlandırılır.
  - Çoklu katman/boyut gerektiren varyantlarda (`stacked`, `stacked100`, `stackedLine`, `stackedArea`, `nested` pasta, `sankey`, `sunburst`, `heatmap`) kullanıcıya dinamik rol etiketleri (`İç Halka`/`Dış Halka`, `Kaynak`/`Hedef`, `X Ekseni`/`Y Ekseni`) ve akıllı ipucu kutuları gösterilir.
- **Hızlı Görsel Anahtarları ve İzleme Ekranı (`app.html`) Senkronizasyonu:**
  - `123 Değerler` (`w.showLabel`), `⎯ Ortalama` (`w.showMarkLine`) ve `📍 Min/Maks` (`w.showMarkPoint`) butonları hem `wwwroot/designer.html` hem de `wwwroot/app.html` üzerinde tüm grafik aileleriyle tam uyumlu hale getirildi.

---

### [x] Adım 12: Analytics Studio Hamburger Menü, Veri Kaynağı / Dashboard Tanımlama Panelleri ve Pasife Alma (`is_active = 0`) Mimarisi (2026-11-08)
- **Analytics Studio Hamburger Menü (`#studioHamburgerMenu` - `wwwroot/designer.html`):**
  - Üst çubukta `📊 Analytics Studio` logosunun soluna `☰` hamburger menü ikonu eklendi ve eski `1. Görsel Tuval | 2. SQL Veri Kaynağı Editörü` sekmeleri kaldırıldı.
  - Menü seçenekleri:
    1. **🗄️ Veri Kaynağı (SQL Datasource):** Tıklandığında doğrudan yeni veri kaynağı oluşturma ekranını (`#sqlEditorView`) açar.
    2. **📐 Dashboard Ekle / Tanımla:** Tıklandığında yeni dashboard oluşturma ve mevcut dashboard bilgilerini düzenleme panelini (`#dashboardEditorView`) açar.
    3. **📊 Görsel Tasarım Tuvaline Dön:** Alt ekranlardayken tasarım tuvaline dönüş sağlar.
- **Üst Çubuk (Topbar) Sadeleştirmesi (`wwwroot/designer.html`):**
  - Eski `#dashMetaBar` alt şeridi (`Dashboard Kodu`, `Başlık`, `Açıklama`), `⚙ Şablon Bilgileri` ve `+ Yeni` butonları kaldırıldı (`🗑️ Tuvali Temizle` butonu tuval araç çubuğuna taşındı).
  - Üst çubuğun sağ tarafındaki `Veri Kaynağı:` açılır kutusu kaldırılarak yerine doğrudan **`Dashboard:`** (`#selExistingDash`) seçim listesi yerleştirildi.
- **SQL Veri Kaynağı Ekranı (`#sqlEditorView`) & Pasife Alma (`Controllers/DataSourceController.cs`, `Services/DynamicQueryService.cs`):**
  - Sağ üstteki gereksiz `+ Yeni Veri Kaynağı` butonu kaldırıldı; sağ üstte yalnızca **`Sil`** (`#btnDeleteDs`) ve **`Kapat`** butonları bırakıldı.
  - Kaydet butonu **`Kaydet`** olarak sadeleştirildi.
  - **`Sil` (Pasife Alma):** `POST /api/datasources/{datasourceId}/deactivate` ucu ile kayıt `bi_datasources` tablosundan fiziksel olarak silinmez, `is_active = 0` yapılarak pasif hale getirilir.
  - Pasif veri kaynağı seçili olan dashboard'larda hem `designer.html` hem de `app.html` üzerinde sorgu ve filtreler çalıştırılmaz, bileşenlerde veri görüntülenmez.
- **Dashboard Tanımlama Paneli (`#dashboardEditorView` - `wwwroot/designer.html`, `Controllers/DashboardController.cs`):**
  - Ekran ilk açıldığında veya `-- Yeni Dashboard Oluştur --` seçildiğinde **Dashboard Kodu**, **Başlık**, **Veri Kaynağı** (`-- Veri Kaynağı Seçin --`) ve **Açıklama** alanları tamamen boş gelir; mevcut bir dashboard seçildiğinde ilgili kaydın verileriyle dolar.
  - **`Durum` (`🟢 Aktif` / `🔴 Pasif`) Seçeneği ve `Sil` Butonu:** Dashboard doğrudan form üzerinden veya `POST /api/dashboards/{dashboardId}/deactivate` ucu ile veritabanından silinmeden aktif/pasif (`is_active`) yapılabilir.

---

### [x] Adım 13: Üst Bar Düzenlemesi, 3. Tema (`💎 SpsTeks Mavi`), Açık Tema Kontrast İyileştirmeleri ve Çoklu Bağlantı Ayarları (2026-11-09)
- **Tasarım Stüdyosu (`wwwroot/designer.html`) Kompakt Üst Bar & Panel Başlıkları:**
  - Üst çubuk (`34px`) ve panel başlıkları (`26px`) daraltılarak tuvale daha fazla çalışma alanı açıldı.
  - Panel başlıkları **`GRAFİK ÖZELLİKLERİ`**, **`KOLONLAR`** ve **`TUVAL ÇALIŞMA ALANI`** olarak güncellendi ve belirgin zemin rengi verildi.
  - `Canlı Veri: ... kayıt, ... kolon` bilgisi `Dashboard:` açılır kutusunun üzerine gelindiğinde (`title` ipucu olarak) gösterilecek şekilde taşındı.
  - `Yetkilendirme` (`/admin`) ve `Veritabanı Ayarları` (`/db-settings`) seçenekleri sol üst `☰` hamburger menüsü altına alındı.
- **3. Tema (`💎 SpsTeks Mavi` - `spsteks-theme`) ve Çoktan Seçmeli Tema Kutusu (`#selThemeMode`):**
  - Hem `wwwroot/designer.html` hem de `wwwroot/app.html` ekranlarına çoktan seçmeli tema açılır kutusu (`🌙 Koyu Tema`, `☀️ Açık Tema`, `💎 SpsTeks Mavi`) eklendi.
  - Açık temadaki okunmayan koyu zeminli alanlar düzeltildi ve NBG Bilişim SpsTeks masaüstü ERP renk paletine uygun 3. tema (`body.spsteks-theme`) oluşturuldu.
- **Çoklu Veritabanı Bağlantı Profili Ekranı (`wwwroot/db-settings.html`, `Services/DbConnectionManager.cs`, `Controllers/DbSettingsController.cs`):**
  - `/db-settings` ekranı sol tarafta kayıtlı bağlantıların listelendiği (`+ Yeni Bağlantı`), sağ tarafta ise bağlantı detaylarının (**Ortam İsmi**, **Açıklama**, **Sunucu**, **Port**, **Veritabanı**, **Kullanıcı**, **Şifre** ve **Bağlantı Aktif** onay kutusu) düzenlendiği iki sütunlu yapıya kavuşturuldu.

---

### [x] Adım 14: Veri Kaynakları Grid & Modal Mimarisi, Güvenlik Sıkılaştırma ve Oturum Çıkışları (2026-11-09)
- **Veri Kaynakları Grid ve Modal Düzenleme (`wwwroot/designer.html`):**
  - `#sqlEditorView` ekranında sol/sağ bölünmüş eski form yerine tam boy, kolon başlıklarına tıklanarak sıralanabilen ve arama kutusuyla filtrelenebilen veri kaynakları tablosu (`#dsMasterGridTable`) oluşturuldu.
  - Bir satır seçilip `✏️ Güncelle` tıklandığında veya satıra çift tıklandığında ekranın ortasında modal iletişim kutusu (`#dsFormSection`) açılır.
  - Veri kaynağına `is_active` alanı bağlandı; pasif yapılan kaynaklar gridde `🔴 Pasif` rozetiyle gösterilir ve tasarım/izleme modunda çalıştırılmaz.
  - `📋 Kopyasını Oluştur` butonu ile canlı veri kaynağının test kopyası tek tıkla üretilir.
- **Oturum Çıkışı (`Çıkış`) ve Tüm Ekranlarda 3'lü Tema:**
  - `wwwroot/designer.html`, `wwwroot/admin.html` ve `wwwroot/db-settings.html` ekranlarına `Çıkış` butonları eklendi; tıklandığında oturum kapatılarak şifre ekranına yönlendirilir.
  - `wwwroot/login.html` ekranına 3'lü tema seçicisi (`🌙 Koyu Tema`, `☀️ Açık Tema`, `💎 SpsTeks Mavi`) eklendi ve seçim `localStorage` ile tüm ekranlar arasında kalıcı kılındı.
- **Veri Kaynağı Ayarları Siber Güvenlik İzolasyonu (`Controllers/DbSettingsController.cs`, `wwwroot/login.html`, `wwwroot/designer.html`):**
  - Login ekranından şifresiz `/db-settings` bağlantısı tamamen kaldırıldı.
  - `/api/db-settings` uçları yalnızca oturum açmış `admin` rolündeki kullanıcılara sınırlandırıldı; yetkisiz çağrılarda `403 Forbidden` döndürülür.
  - `designer.html` ve `admin.html` arayüzlerinde admin olmayan kullanıcılara veri kaynağı ayarları menüsü gizlendi.

---

### [x] Adım 15: Viewer (İzleme) Ekranı Dikey Parametre Paneli ve Alt Durum Çubuğu (Status Bar) Mimarisi (2026-11-09)
- **Kompakt Üst Çubuk (36px) ve Hamburger Menü (`wwwroot/app.html`):**
  - Viewer üst başlığı daraltıldı (`36px`), sol tarafa `☰` hamburger menüsü yerleştirildi.
  - Hamburger menüye oturum açan kullanıcının yetkisine göre Tasarım Stüdyosu (`/designer`) ve Yetki Yönetimi (`/admin`) kısayolları eklendi.
- **Sol Dikey, Gizlenebilir ve Mouse ile Boyutlandırılabilir Parametre Paneli (`#controlBarSection`):**
  - Parametre alanı üst şeritten sol dikey panele taşındı, parametreler alt alta hizalandı.
  - Slayt Modu, Otomatik Yenileme ve Göster butonları SVG ikon butonlarına dönüştürüldü; üzerine gelindiğinde açıklaması gösterilir.
  - Panel `«` butonu veya dikey şeridi ile tamamen gizlenip açılabilir.
  - `#sidebarResizer` ayırıcı çizgisi ile kullanıcı paneli mouse ile 180px–520px arasında istediği genişliğe çekebilir; genişlik `localStorage` içinde saklanır.
- **Alt Durum Çubuğu (Status Bar) ile Cross-Filter ve Kayıt Takibi (`#viewerStatusBar`):**
  - Üst tarafta bulunan ve grafikleri aşağı iten eski rozet şeridi kaldırıldı.
  - Ekranın en altına sabit, her temada net okunan 26px yüksekliğinde durum çubuğu eklendi:
    - 🔎 **Filtre:** Grafiğe tıklandığında oluşan filtreler kolon formatı ve değeriyle rozet olarak listelenir (örn: `Cari Adı = ABC ✕`), tek tıkla kaldırılabilir.
    - **Kayıt Sayacı:** Filtrelenen ve toplam satır sayısını dinamik gösterir (örn: `124 / 3.560`).
    - **Tümünü Temizle:** Yalnızca aktif filtre varken görünen tek tık temizleme butonu.

---

### [x] Adım 16: BI Veritabanı ve İşletme Veritabanı Bağlantı Mimarisi Ayrımı (2026-11-10)
- **BI Sistem Ayarları (`/bi-settings`, `appsettings.bi.json`):**
  - BI sisteminin kullanıcıları, dashboard'ları ve yetkileri barındıran kendi tabloları (`bi_*`) işletme ERP/SQL bağlantılarından tamamen izole edildi.
  - `Services/BiDbConnectionManager.cs`, `Controllers/BiSettingsController.cs` ve `wwwroot/bi-settings.html` oluşturuldu; şifreler Windows DPAPI (`ENC:...`) ile korunmaktadır.
- **İşletme Veri Kaynakları Ekranı Düzenlemesi (`/db-settings`):**
  - `Controllers/DbSettingsController.cs` içinden `bi_*` tablo doğrulamaları kaldırıldı, sadece işletme SQL bağlantılarını yönetecek şekilde sadeleştirildi.
  - `wwwroot/db-settings.html` sağ panel yerleşimi (`320px 580px`) ve başlık hizalaması taşma yapmayacak şekilde düzeltildi; "Kapat" ve "Çıkış" butonları eklendi.

---

### [x] Adım 17: Yetkisiz Kullanıcı Açılış Ekranı İyileştirmesi (`wwwroot/app.html` - 2026-11-10)
- Kullanıcının yetkili olduğu aktif bir dashboard bulunmadığında çıkan bildirim alanı 120 kolon tam tuvale yayıldı, ortalandı ve kurumsal kilit ikonu (`🔒`) ile *"HESABINIZA TANIMLANMIŞ AKTİF BİR DASHBOARD YETKİSİ BULUNMAMAKTADIR"* mesajı verildi.

---

### [x] Adım 18: 39 Alt Grafik Görsel Stil Galeri Paneli (`🎨 Galeri`), Özel SVG Vektör Çizimleri ve Kalıcı Hafıza Mekanizması (2026-11-10)
- **Görsel Alt Stil Galerisi Paneli (`#subTypeGalleryPopover`):**
  - 8 ana grafik ailesi (`bar`, `line`, `pie`, `gauge`, `hierarchy`, `funnel`, `radar`, `scatter`) altındaki tüm 39 varyant için temaya duyarlı, yüksek çözünürlüklü SVG vektör ikonları kodlandı (`SUBTYPE_SVG_CATALOG`).
  - İsteğe bağlı özel PNG/SVG görsel yüklemek isteyen kullanıcılar için `wwwroot/img/charts/` klasörü ve `README.txt` kılavuzu hazırlandı.
- **Grafik Başlıklarına `🎨 Galeri` Butonu Entegrasyonu:**
  - Hem Tasarım Stüdyosu (`designer.html`) hem de İzleme Ekranı (`app.html`) üzerindeki her grafik kartının başlığına tek tıkla açılan `🎨 Galeri` butonu eklendi.
  - Popover modalın tuval arkasında kalmaması için `body` seviyesine taşındı (`z-index: 9999`).
- **Kalıcı Hafıza Mekanizması (`localStorage`):**
  - Kullanıcı veya tasarımcı galeri panelinden bir stil seçtiğinde seçim `localStorage.setItem('sps_widget_subtype_${dashId}_${widgetId}', subType)` ile saklanır.
  - Sayfa yenilendiğinde veya tarayıcı kapatılıp tekrar açıldığında ilgili grafik doğrudan son seçilen alt stil ile yüklenir.
- **Tasarım Paneli Sadeleştirmesi:**
  - Sol özellikler panelindeki mükerrer `🎨 Galeri` butonu kaldırılarak fonksiyon tamamen grafik başlıklarına devredildi.

---

### [x] Adım 19: Git Sürüm Kontrolü ve GitHub Senkronizasyonu (2026-11-10)
- Tüm yeni dosyalar (`BiSettingsController.cs`, `BiDbConnectionManager.cs`, `appsettings.bi.json`, `bi-settings.html`, `img/charts/` ve güncellenen frontend/backend dosyaları) sahnelendi.
- `c669c6b` commit hash'i ile `feat: BI ve Veri Kaynagi ayarlarinin ayrilmasi, 39 alt grafik gorsel galeri paneli ve kalici hafiza entegrasyonu` başlığıyla kaydedildi.
- `https://github.com/KCanakli/SpsWebBI.git` deposundaki `master` dalına başarıyla push edildi.




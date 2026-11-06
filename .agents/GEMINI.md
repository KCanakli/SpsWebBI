# SpsWebBI Projesi Kuralları ve Kalıcı Hafıza

Bu dosya, `c:\Projeler\SpsWebBI` projesinde alınan tüm mimari, siber güvenlik ve iş kurallarını yeni oturumlarda eksiksiz hatırlamak için oluşturulmuştur.

## 1. Çalışma Prensibi (Zorunlu)
- Her adımdan veya değişiklikten önce kullanıcıya **ne yapılacağı ve neden yapılacağı** anlatılacak, onay alındıktan sonra uygulanacaktır.

## 2. Mimari ve Veritabanı Özeti
- **Proje Dizini:** `c:\Projeler\SpsWebBI` (.NET 8.0 Web Host App, Dapper, MySqlConnector, yerel Apache ECharts 5).
- **MariaDB Bağlantısı:** `127.0.0.1`, Port: `3323`, Veritabanı: `spsteks`, Kullanıcı: `root` (Şifre `appsettings.json` içinde Windows DPAPI `ENC:...` ile şifrelidir; `/db-settings` ekranından değiştirilebilir).
- **Sıfır C# DTO Kuralı (Madde 2.5):** Yeni raporlar için C# sınıfı yazılmaz; `Services/DynamicQueryService.cs` SQL sorgularından kolon ve veri tiplerini dinamik çıkarır.
- **Şifreli Token Güvenliği (`Security=0`):** Düz metin `?User=A&Security=0` ile şifresiz giriş reddedilir. Şifresiz giriş yalnızca `Security/UserTokenProtector.cs` ile üretilen AES-256 + HMAC-SHA256 imzalı token (`UT1_...`) ve `allow_passwordless = 1` yetkisi ile çalışır; giriş anında adres çubuğu `history.replaceState` ile temizlenir ve gerçek `user_id` Cookie oturumuna yazılır.

## 3. Varsayılan Hesaplar ve Hazır Raporlar
- **`admin`:** Şifre `Admin123!` (Sistem Yöneticisi)
- **`A`:** Şifre/PIN `1234` (`allow_passwordless = 1`, Token: `UT1_Pk9kKv_b7FwpHEwEjPV1s6QRKxsC7mXooD0n8k2TD8Y6fCQnaFxX1J2RAKM`)
- **Hazır Dashboard'lar:** `DASH_SIPARIS` (`DS_SIPARIS` - Satış & Sipariş) ve `DASH_STOK` (`DS_STOK` - Depo & Stok Durum).

## 4. Referans Dosyalar
- Detaylı adım özeti: `c:\Projeler\SpsWebBI\PROJE_DURUMU.md`
- Tam sohbet geçmişi: `c:\Projeler\SpsWebBI\Sohbet_Gecmisi_2026-11-05.txt` ve `c:\Projeler\SpsWebBI\Sohbet_Gecmisi_2026-11-05.pdf`

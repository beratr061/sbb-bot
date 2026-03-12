# 🚍 SBB Bot - Proje Analiz Raporu

**Proje Adı:** SBB Bot (Sakarya Büyükşehir Belediyesi Ulaşım Asistanı)
**Platform / Dil:** .NET (C#) -> .NET 8.0 SDK kullanılmış, ancak .NET 10 hedefleniyor
**Mimari:** `Microsoft.Extensions.Hosting` tabanlı Arka Plan Worker Servis Mimarisi
**Amaç:** Sakarya Büyükşehir Belediyesi'nin dışa açık tüm ulaşım verilerini (SBB Public API, HTML Web sayfaları ve CKAN Açık Veri Portalı) dinleyip Telegram botu üzerinden abonelere/gruplara "Canlı Bilgi" sağlamak ve "Anlık Değişiklikleri" bildirmek.

---

## 🏗️ 1. Sistem Mimarisi & Klasör Yapısı

Sistem, birden fazla paralel görevin birbirinden bağımsız çalıştığı (BackgroundWatcher) ve bir yandan da Telegram mesajlarının karşılanıp/yanıtlandığı modüler bir yapıdan oluşmaktadır.

### 📂 Temel Dizinler
*   **`Services/`**: Sistemdeki tüm işlemleri yöneten ana sınıfların (`BackgroundService` ve yardımcı servisler) tutulduğu kısımdır. Sistemdeki "İzleyiciler" (Watchers) burada bulunur.
*   **`Data/`**: Herhangi bir RDBMS (SQL vb.) kullanılmamış; onun yerine bir dosya tabanlı (JSON tipli) durum makinesi (State Management) kurulmuştur. Bot, önceki verileri JSON'dan okur, güncel veriyle `Hash` veya liste karşılaştırması yaparak farkları (yeni hat, fiyat değişimi, yeni karar vb.) tespit eder.
*   **`Models/`**: API'den gelen verilerin C# diline (`JsonSerializer` vb. aracılığıyla) deserialize (bind) edildiği düz (POCO) Entity sınıflarını tutar.
*   **`Helpers/`**: Telegram metinlerini şekillendiren, Telegram API limitlerini veya Telegram kurallarını handle eden (`TelegramHelper` vb.) sınıfları tutar.
*   **Python Betikleri (`*.py`)**: SBB Public API'sinin uç noktalarını (endpoints) keşfetmek, ASIS araç takip numaraları ile SBB hat kimliklerini eşleştirmek (örn: `map_asis.py`, `discover_endpoints.py`) için yazılmış keşif (discovery) amaçlı araçlar.

---

## ⚙️ 2. Core Bileşenler ve Görevleri

Sistem çalışma zamanında (Runtime) bağımlılık enjeksiyonu (`DI`) mimarisiyle `Program.cs` üzerinde ayağa kaldırılır.  

### Ana Yönetim ve Dinleme Sınıfları

1.  **`TelegramListenerService.cs`** 
    *   Telegram'ın Long Polling (Uzun yoklama) yöntemi üzerinden (`GetUpdatesAsync`) mesajları dinler. Webhook kullanılmamıştır. Mesaj geldikçe bunu `InteractionManager`'a iletir.
2.  **`InteractionManager.cs`**
    *   Sistemin "Kullanıcı Etkileşimi" beynidir (~700+ satır). Gelen Telegram komutlarını (/istatistik, sonraki otobüs, durak seçimi, vs.) anlar ve kullanıcıya inline keyboard (butonlu menüler) içeren yanıtlar hazırlar. Callback sorgularını (buton tıklamalarını) yönetir.
3.  **`VehicleService.cs`**
    *   Araçların canlı konumlarını anlık çeken servistir. ASIS altyapısını kullanır ve ASIS id'leri üzerinden canlı araç takibini yapar (Otobüs durağa yaklaştı mı, seyir halinde mi vb.).

### Periyodik İzleyici Servisler (`Watchers`)

*Bu servislerin her biri, `appsettings.json` üzerinden ayarlanan dakika aralıklarıyla (`Intervals`) sonsuz bir döngüde çalışır (`Task.Delay` ile).*

*   **`BusLineWatcherService`**: Yeni açılan veya kapanan hatları (Özel Halk, Taksi Dolmuş vb.) denetler. Güzergah listesindeki değişiklikleri anlık bulur.
*   **`RouteWatcherService`**: Bir hatta ait duraklar ve güzergah yörüngesinde sapma ya da değişme var mı diye kontrol eder.
*   **`FareWatcherService`**: Fiyat tarifelerindeki güncellemeleri kontrol eder. Eğer bir hatta zam/indirim yapıldıysa sistemin genelini uyaracak hash (SHA-256) doğrulaması kullanır.
*   **`AnnouncementWatcherService`**: Sakarya Ulaşım departmanının yaptığı anlık sefer/hat/güzergah iptali veya duyurularını izler.
*   **`UkomeWatcherService`, `MeetingWatcherService`, `OpenDataWatcherService`, `DocumentWatcherService`, `NewsWatcherService`**: 
    *   Mevcut Sakarya Belediyesi web siteleri üzerinden **Web Scraping (`HtmlAgilityPack`)** yöntemiyle sayfa HTML elemanlarını kazıyarak UKOME kararlarını (PDF'ler), stratejik dökümanları, meclis kararlarını veya haberi alır. API olmayan yerlerde otomasyon sağlar. Ayrıca Sakarya CKAN tabanlı Açık Veri Portalı da izlenerek yeni yayınlanan datasatler tespit edilir.

---

## 🚀 3. Veri Akışı ve Önlem Mekanizmaları

1.  **Dış API İstekleri (CORS ve Kimlik)**: SBB Public API, standart dışarıdan kontrollere kapalı olduğu için yazılım `Origin` ve `Referer` (`https://ulasim.sakarya.bel.tr`) HTTP headerlarını taklit (spoof) ederek çalışır.
2.  **Veri Keşfi ve Haritalama**: Kurumun kendi içinde Hat ID'si ile (Örn: 10 numaralı hat) Araç ID'si (ASIS ID: 35) farklı çalışmaktadır. Projedeki `map_asis.py` scripti 1'den 400'e kadar tüm ASIS ID'lerini tarayıp hangi ASIS ID'nin hangi Hat Numarasına ait olduğunu bulur ve C#'ın tüketmesi için bu metadatayı `Data/asis_map.json` ya da `Data/vehicle_map.json`'a çıkarır.
3.  **Hata Yönetimi (Resilience/Polly C#)**: Fiyat ve güzergah watcher vb. alt servislerde internet kopmaları ya da SBB API 502/503 (Sunucu çökmeleri) durumlarına karşı kod içi otomatik yeniden deneme esneklik mekanizmaları mevcuttur.
4.  **Loglama**: `Serilog` kullanılarak tüm izleyicilerin başardığı ya da hata (Exception) aldığı task'lar anlık loglanır ve konsola/file'a iletebilir (Şu an Configürayon appsettings üzerinden beslenmektedir).

---

## 📈 4. Analiz ve İyileştirme Önerileri (Teknik Borç / Technical Debt)

Projeyi baştan aşağı incelediğimizde güçlü ve modüler bir mimari kurgulanmış olsa da gelecekteki ölçeklenebilirlik (scalability) için şu konulara dikkat edilebilir:

*   **Veritabanı İhtiyacı:** Aşırı büyüme senaryosunda json dosya I/O (Okuma/Yazma) işlemleri disk darboğazı yaratabilir. İlerleyen günlerde `.json` saklama yapısı **SQLite** ya da **PostgreSQL** gibi bir ilişkisel veya **Redis** (özellikle canlı araç veri cache'i ve pub/sub için) gibi bir yapıya devredilebilir.
*   **InteractionManager'ın Şişkinliği**: `InteractionManager.cs` şu anda tek bir sınıfta tüm Telegram callback logic'ini, UI (buton oluşturma) kodlarını ve Telegram mesaj biçimlendirmelerini barındırıyor (God Object antipattern'ine doğru kayma). "Command / Handler" tasarım desenine (Design Pattern) geçirilerek klasör yapısı `Commands/LineCommand`, `Commands/DashboardCommand` vb. şeklinde küçültülebilir.
*   **Kullanıcı Bazlı Profilleme**: Data klasörü şu anda yalnızca genel state (durum) tutuyor. Fakat bireysel Telegram abone (ChatID) durumlarını tutan bir üye yönetimi altyapısı da entegre edilirse, "sadece benim bindiğim hattı bana bildir" tarzı gelişmiş yetenekler kolayca uygulanabilir.

## 🎯 5. Sonuç
SBB Bot, .NET Core Worker Service modelinin çok başarılı ve asenkron (async) şekilde kullanıldığı, yerel API'leri tersine mühendislikle (Reverse Engineering - Header/Referer Spoof ve Brute-force ID Mapping) çözen oldukça kapasiteli (Canlı İzleme, Tarife İzleme, Bürokratik Haber İzleme) entegre bir otomasyon sistemidir.

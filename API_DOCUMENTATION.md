# 📡 SBB Bot — Kullanılan API'ler ve Özellikler

Bu belge, SBB Bot projesinde kullanılan tüm dış API'leri, HTTP kaynaklarını ve servisleri detaylı olarak açıklamaktadır.

> 📅 Son Güncelleme: 19 Şubat 2026

---

## 📑 İçindekiler

1. [Telegram Bot API](#1--telegram-bot-api)
2. [SBB Public API — Ulaşım Hat Listesi](#2--sbb-public-api--ulaşım-hat-listesi)
3. [SBB Public API — Hat Sefer Saatleri](#3--sbb-public-api--hat-sefer-saatleri)
4. [SBB Public API — Güzergah ve Durak Bilgisi](#4--sbb-public-api--güzergah-ve-durak-bilgisi)
5. [SBB Public API — Fiyat Tarifesi](#5--sbb-public-api--fiyat-tarifesi)
6. [SBB Public API — Duyurular](#6--sbb-public-api--duyurular)
7. [SBB Public API — Araç Takip (Canlı)](#7--sbb-public-api--araç-takip-canlı)
8. [Sakarya Büyükşehir — Haberler (HTML Scraping)](#8--sakarya-büyükşehir--haberler-html-scraping)
9. [Sakarya Büyükşehir — Meclis Kararları (HTML Scraping)](#9--sakarya-büyükşehir--meclis-kararları-html-scraping)
10. [Sakarya Büyükşehir — UKOME Kararları (HTML Scraping)](#10--sakarya-büyükşehir--ukome-kararları-html-scraping)
11. [Sakarya Büyükşehir — Stratejik Planlama Belgeleri (HTML Scraping)](#11--sakarya-büyükşehir--stratejik-planlama-belgeleri-html-scraping)
12. [Sakarya Açık Veri Portalı (CKAN API)](#12--sakarya-açık-veri-portalı-ckan-api)

---

## 1. 🤖 Telegram Bot API

| Özellik | Değer |
|---------|-------|
| **Kütüphane** | `Telegram.Bot` (NuGet) |
| **Base URL** | `https://api.telegram.org/bot{TOKEN}/` |
| **Yetkilendirme** | Bot Token (`appsettings.json` → `Telegram:Token`) |

### Kullanılan Servisler
| Servis Dosyası | Açıklama |
|----------------|----------|
| `TelegramListenerService.cs` | Polling ile gelen mesajları dinler |
| `InteractionManager.cs` | Kullanıcı komutlarını işler, yanıt oluşturur |
| `TelegramHelper.cs` | Bildirim mesajları gönderir |

### Kullanılan İşlemler
- **Polling**: `GetUpdatesAsync` — Yeni mesaj ve callback sorguları dinlenir
- **SendMessage**: Kullanıcıya mesaj gönderme
- **EditMessageText**: Mevcut mesajı güncelleme (inline butonlar için)
- **InlineKeyboardMarkup**: Butonlu interaktif menüler oluşturma
- **CallbackQuery**: Buton tıklamalarını işleme

### Konfigürasyon
```json
{
  "Telegram": {
    "Token": "BOT_TOKEN",
    "ChatId": "KANAL_CHAT_ID"
  }
}
```

---

## 2. 🚌 SBB Public API — Ulaşım Hat Listesi

| Özellik | Değer |
|---------|-------|
| **URL** | `https://sbbpublicapi.sakarya.bel.tr/api/v1/Ulasim?busType={busType}` |
| **Metod** | `GET` |
| **Yetkilendirme** | Yok (Public) |
| **Gerekli Header** | `Origin: https://ulasim.sakarya.bel.tr`, `Referer: https://ulasim.sakarya.bel.tr` |

### Parametreler
| Parametre | Tür | Açıklama |
|-----------|-----|----------|
| `busType` | `int` | Araç tipi kodu. Bilinen değerler: `5731` (Özel Halk Otobüsü), `3869` (Belediye), `5733` (Taksi Dolmuş), `5742` (Minibüs) |

### Kullanıldığı Servisler
| Servis | Amaç |
|--------|------|
| `BusLineWatcherService.cs` | Hat listesi değişikliklerini izler, yeni eklenen/kaldırılan hatları bildirir |
| `RouteWatcherService.cs` | Güzergah değişiklikleri için ha listesini çeker |
| `InteractionManager.cs` | Kullanıcıya hat listesi gösterme |

### Yerel Depolama
- **Dosya**: `Data/bus_lines.json`
- **İçerik**: Hat adları kategorilere göre (özel halk, belediye, taksi dolmuş, minibüs)

---

## 3. ⏰ SBB Public API — Hat Sefer Saatleri

| Özellik | Değer |
|---------|-------|
| **URL** | `https://sbbpublicapi.sakarya.bel.tr/api/v1/Ulasim/line-schedule?date={date}&lineId={lineId}` |
| **Metod** | `GET` |
| **Yetkilendirme** | Yok (Public) |
| **Gerekli Header** | `Origin`, `Referer` (ulasim.sakarya.bel.tr) |

### Parametreler
| Parametre | Tür | Format | Açıklama |
|-----------|-----|--------|----------|
| `date` | `string` | `yyyy-MM-ddT00:00:00.000Z` | Tarih (URL-encoded) |
| `lineId` | `int` | — | Hat ID'si |

### Kullanıldığı Servisler
| Servis | Amaç |
|--------|------|
| `BusLineWatcherService.cs` | Sefer saati değişikliklerini izler ve bildirim gönderir |
| `InteractionManager.cs` | Kullanıcıya sefer saatlerini gösterir |

### Yerel Depolama
- **Dosya**: `Data/schedules/{subfolder}/{lineName}.json`
- **İçerik**: Gün tipine göre sefer saatleri (Hafta içi, Cumartesi, Pazar)

---

## 4. 🗺️ SBB Public API — Güzergah ve Durak Bilgisi

| Özellik | Değer |
|---------|-------|
| **URL** | `https://sbbpublicapi.sakarya.bel.tr/api/v1/Ulasim/route-and-busstops/{lineId}?date={date}` |
| **Metod** | `GET` |
| **Yetkilendirme** | Yok (Public) |
| **Gerekli Header** | `Origin`, `Referer` (ulasim.sakarya.bel.tr) |

### Parametreler
| Parametre | Tür | Açıklama |
|-----------|-----|----------|
| `lineId` | `int` | Hat ID'si |
| `date` | `string` | Tarih (`yyyy-MM-dd` formatında) |

### Yanıt İçeriği
- Hattın güzergah geometrisi (koordinatlar)
- Durak listesi (durak adı, sırası, koordinatları)
- Hat yönleri (Gidiş/Dönüş)

### Kullanıldığı Servisler
| Servis | Amaç |
|--------|------|
| `RouteWatcherService.cs` | Güzergah ve durak değişikliklerini izler |

### Yerel Depolama
- **Dosya**: `Data/routes/{lineId}.json`
- **İçerik**: Durak listesi, güzergah bilgisi

---

## 5. 💰 SBB Public API — Fiyat Tarifesi

| Özellik | Değer |
|---------|-------|
| **URL** | `https://sbbpublicapi.sakarya.bel.tr/api/v1/Ulasim/line-fare/{lineId}?busType=3869` |
| **Metod** | `GET` |
| **Yetkilendirme** | Yok (Public) |
| **Gerekli Header** | `Origin`, `Referer` (ulasim.sakarya.bel.tr) |

### Parametreler
| Parametre | Tür | Açıklama |
|-----------|-----|----------|
| `lineId` | `int` | Hat ID'si |
| `busType` | `int` | Araç tipi kodu (`3869`) |

### Yanıt Yapısı
```json
{
  "tariffList": [
    { "lineFareTypeId": 38, "typeName": "Tam" },
    { "lineFareTypeId": 39, "typeName": "Öğrenci" }
  ],
  "groups": [
    {
      "name": "Grup Adı",
      "routes": [
        {
          "routeName": "Güzergah Adı",
          "baseFare": 15.00,
          "tariffs": [
            { "lineFareTypeId": 38, "finalFare": 25.00 }
          ]
        }
      ]
    }
  ]
}
```

### Kullanıldığı Servisler
| Servis | Amaç |
|--------|------|
| `FareWatcherService.cs` | Referans hattın (ID: 137) fiyat değişikliklerini izler, hash karşılaştırır |
| `InteractionManager.cs` | Kullanıcıya tarife bilgisini gösterir |

### Yerel Depolama
- **Cache**: `Data/fare_cache/{lineId}.json` — 24 saat geçerli önbellek
- **State**: `fare_state.json` — Değişiklik tespiti için SHA-256 hash

---

## 6. 📢 SBB Public API — Duyurular

| Özellik | Değer |
|---------|-------|
| **URL** | `https://sbbpublicapi.sakarya.bel.tr/api/v1/Ulasim/announcement?pageSize=100` |
| **Metod** | `GET` |
| **Yetkilendirme** | Yok (Public) |
| **Gerekli Header** | `Origin`, `Referer` (ulasim.sakarya.bel.tr) |

### Yanıt İçeriği
- `items[]` dizisi içinde duyurular:
  - `id`, `title`, `content` (HTML), `slug`
  - `lineId`, `lineNumber`, `lineName`
  - `categoryName` (örn: "Genel Duyuru")
  - `startDate`, `endDate` (ISO 8601)

### Kullanıldığı Servisler
| Servis | Amaç |
|--------|------|
| `AnnouncementWatcherService.cs` | Yeni duyuruları izler, değişiklikleri Telegram'a bildirir |
| `InteractionManager.cs` | Belirli bir hat için duyuruları gösterir (JSON'dan okur) |

### Yerel Depolama
- **State**: `Data/announcements.json` — Görülmüş duyuruların hash'leri (`{id: hash}`)
- **Data**: `Data/announcement_data.json` — API'den gelen ham JSON verisi

---

## 7. 🛰️ SBB Public API — Araç Takip (Canlı)

| Özellik | Değer |
|---------|-------|
| **URL** | `https://sbbpublicapi.sakarya.bel.tr/api/v1/VehicleTracking?AsisId={asisId}` |
| **Metod** | `GET` |
| **Yetkilendirme** | Yok (Public) |
| **Gerekli Header** | `Origin`, `Referer` (ulasim.sakarya.bel.tr) |

### Parametreler
| Parametre | Tür | Açıklama |
|-----------|-----|----------|
| `AsisId` | `int` | Hattın ASIS takip ID'si (hat ID'si ile aynı değildir, eşleme `Data/vehicle_map.json`'da yapılır) |

### Yanıt İçeriği (VehicleLocation)
| Alan | Tür | Açıklama |
|------|-----|----------|
| `busNumber` | `string` | Araç plaka/numarası |
| `lineNumber` | `string` | Hat numarası |
| `status` | `string` | Araç durumu: `CRUISE`, `AT_STOP`, `APPROACH`, `DEPARTURE`, `IDLE`, `OUT_OF_SERVICE` |
| `latitude` | `double` | Enlem |
| `longitude` | `double` | Boylam |
| `nextStopName` | `string` | Sonraki durak adı |
| `nextStopDistance` | `double` | Sonraki durağa mesafe (metre) |
| `currentStopName` | `string` | Mevcut durak adı |

### Türkçe Durum Çevirileri
| API Değeri | Türkçe Karşılık |
|------------|-----------------|
| `CRUISE` | Seyir Halinde 🟢 |
| `AT_STOP` | Durakta 🔵 |
| `APPROACH` | Durağa Yaklaşıyor 🟡 |
| `DEPARTURE` | Duraktan Ayrıldı 🟠 |
| `IDLE` | Beklemede ⚪ |
| `OUT_OF_SERVICE` | Servis Dışı 🔴 |

### Kullanıldığı Servisler
| Servis | Amaç |
|--------|------|
| `VehicleService.cs` | Canlı araç konumlarını çeker |
| `InteractionManager.cs` | "Sonraki Otobüs" komutu ile kullanıcıya gösterir |

### Özel Notlar
- API bazen `busNumber` ve `lineNumber` alanlarını JSON number olarak gönderir (string yerine). Bu durum `FlexibleStringConverter` ile ele alınır.
- Hat ID → ASIS ID eşlemesi `Data/vehicle_map.json` dosyasından okunur.

---

## 8. 📰 Sakarya Büyükşehir — Haberler (HTML Scraping)

| Özellik | Değer |
|---------|-------|
| **URL** | `https://sakarya.bel.tr/tr/Haberler/1` |
| **Metod** | `GET` (HTML Scraping) |
| **Yetkilendirme** | Yok |

### Kullanıldığı Servisler
| Servis | Amaç |
|--------|------|
| `NewsWatcherService.cs` | Yeni haberleri tespit eder ve Telegram'a bildirir |

### Yerel Depolama
- `Data/news.json` — Daha önce bildirilen haberlerin başlıkları (HashSet)
- `Data/last_news.txt` — Son gönderilen haberin başlığı

### Not
- Bu bir API değil, HTML sayfası parse edilerek haber başlıkları ve bağlantıları çıkarılır.

---

## 9. 🏛️ Sakarya Büyükşehir — Meclis Kararları (HTML Scraping)

| Özellik | Değer |
|---------|-------|
| **URL** | `https://www.sakarya.bel.tr/tr/EBelediye/MeclisKararlari` |
| **Metod** | `GET` (HTML Scraping) |
| **Yetkilendirme** | Yok |

### Kullanıldığı Servisler
| Servis | Amaç |
|--------|------|
| `MeetingWatcherService.cs` | Yeni meclis kararlarını tespit eder ve bildirir |

### Yerel Depolama
- `Data/meetings.json` — Bildirilen kararların ID'leri (HashSet)

---

## 10. ⚖️ Sakarya Büyükşehir — UKOME Kararları (HTML Scraping)

| Özellik | Değer |
|---------|-------|
| **URL** | `https://sakarya.bel.tr/tr/Anasayfa/UkomeKararlari` |
| **Metod** | `GET` (HTML Scraping) |
| **Yetkilendirme** | Yok |
| **Base URL** | `https://sakarya.bel.tr` (PDF bağlantıları için) |

### Kullanıldığı Servisler
| Servis | Amaç |
|--------|------|
| `UkomeWatcherService.cs` | Yeni UKOME kararlarını (PDF) tespit eder ve bildirir |

### Yerel Depolama
- `Data/ukome.json` — Bildirilen kararların URL'leri (HashSet)
- `Data/ukome_years.json` — Kontrol edilen yıllar

---

## 11. 📄 Sakarya Büyükşehir — Stratejik Planlama Belgeleri (HTML Scraping)

| Özellik | Değer |
|---------|-------|
| **URL** | `https://www.sakarya.bel.tr/tr/StratejikPlanlama` |
| **Metod** | `GET` (HTML Scraping) |
| **Yetkilendirme** | Yok |

### Kullanıldığı Servisler
| Servis | Amaç |
|--------|------|
| `DocumentWatcherService.cs` | Yeni belgeleri tespit eder ve bildirir |

### Yerel Depolama
- `Data/documents.json` — Bildirilen belgelerin başlıkları (HashSet)

---

## 12. 📊 Sakarya Açık Veri Portalı (CKAN API)

| Özellik | Değer |
|---------|-------|
| **URL** | `https://veri.sakarya.bel.tr/api/3/action/group_activity_list?id=ulasim` |
| **Metod** | `GET` |
| **Yetkilendirme** | Yok (Public) |
| **API Tipi** | CKAN Action API |

### Parametreler
| Parametre | Tür | Açıklama |
|-----------|-----|----------|
| `id` | `string` | Grup ID'si (`ulasim` = Ulaşım grubu) |

### Kullanıldığı Servisler
| Servis | Amaç |
|--------|------|
| `OpenDataWatcherService.cs` | Açık veri portalında ulaşım grubundaki yeni veri setlerini izler |

### Detay Linki
- `https://veri.sakarya.bel.tr/dataset/{objectId}`

---

## 🔧 Ortak HTTP Header'lar

SBB Public API'ye yapılan tüm isteklerde aşağıdaki header'lar zorunludur:

```http
Origin: https://ulasim.sakarya.bel.tr
Referer: https://ulasim.sakarya.bel.tr
User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 ...
```

> ⚠️ Bu header'lar olmadan API istekleri CORS veya 403 hatasıyla reddedilebilir.

---

## 📁 Yerel Veri Dosyaları Özeti

| Dosya | Tür | Açıklama |
|-------|-----|----------|
| `Data/bus_lines.json` | Hat Listesi | Kategorilere göre hat adları |
| `Data/schedules/{sub}/{name}.json` | Sefer Saatleri | Hat bazlı günlük saatler |
| `Data/routes/{lineId}.json` | Güzergah | Durak listesi ve koordinatlar |
| `Data/fare_cache/{lineId}.json` | Tarife Cache | 24 saatlik API önbelleği |
| `Data/fare_state.json` | Tarife State | Değişiklik tespiti hash'i |
| `Data/announcements.json` | Duyuru State | Görülmüş duyuru hash'leri |
| `Data/announcement_data.json` | Duyuru Data | Tüm aktif duyurular (API yanıtı) |
| `Data/vehicle_map.json` | Araç Eşleme | Hat ID → ASIS ID eşlemesi |
| `Data/news.json` | Haber Geçmişi | Bildirilen haber başlıkları |
| `Data/last_news.txt` | Son Haber | En son gönderilen haber |
| `Data/meetings.json` | Meclis | Bildirilen karar ID'leri |
| `Data/ukome.json` | UKOME | Bildirilen karar URL'leri |
| `Data/ukome_years.json` | UKOME Yılları | Kontrol edilen yıllar |
| `Data/documents.json` | Belgeler | Bildirilen belge başlıkları |

---

## ⏱️ Polling Aralıkları

Arka plan servisleri periyodik olarak API'leri sorgular. Aralıklar `appsettings.json` → `Intervals` bölümünden ayarlanır:

| Servis | Varsayılan Aralık | Ayar Adı |
|--------|--------------------|----------|
| Haber İzleme | 15 dakika | `NewsMinutes` |
| Duyuru İzleme | 10 dakika | `AnnouncementMinutes` |
| Hat Değişiklik İzleme | 60 dakika | `BusLineMinutes` |
| Sefer Saati İzleme | 60 dakika | `ScheduleMinutes` |
| Tarife İzleme | 120 dakika | `FareMinutes` |
| Güzergah İzleme | 120 dakika | `RouteMinutes` |
| Meclis Kararları | 360 dakika | `MeetingMinutes` |
| UKOME Kararları | 360 dakika | `UkomeMinutes` |
| Belgeler | 360 dakika | `DocumentMinutes` |
| Açık Veri | 360 dakika | `OpenDataMinutes` |

---

## 🛡️ Hata Yönetimi

- **Polly Retry**: `FareWatcherService` retry policy kullanır (başarısız istekler otomatik tekrarlanır)
- **FlexibleStringConverter**: VehicleService'te tutarsız JSON türlerini ele alır
- **Hash Karşılaştırma**: Değişiklik tespiti için SHA-256 hash kullanılır (gereksiz bildirim önlenir)
- **Cache Mekanizması**: Fare verisi 24 saat cache'lenir, API'ye gereksiz istek gitmez

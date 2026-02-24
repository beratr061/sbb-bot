# 🚍 SBB Bot - Sakarya Büyükşehir Belediyesi Ulaşım Asistanı

SBB Bot, Sakarya Büyükşehir Belediyesi'nin sunduğu tüm toplu taşıma verilerini (Otobüs, Özel Halk, Minibüs, Taksi Dolmuş) anlık olarak takip eden, analiz eden ve değişiklikleri bildiren gelişmiş bir Telegram botudur.

## 🌟 Özellikler

Bot, aşağıdaki veri kaynaklarını belirlenen periyotlarla tarar ve değişiklik olduğunda bildirim gönderir:

### 1. 🚌 Hat ve Güzergah Takibi
*   **Kapsam:** Belediye Otobüsleri, Özel Halk Otobüsleri, Minibüsler ve Taksi Dolmuşlar.
*   **İzlenen Değişiklikler:**
    *   🆕 Yeni açılan hatlar.
    *   ❌ Kaldırılan hatlar.
    *   📍 Güzergah değişiklikleri (Gidiş/Dönüş yönlerindeki güncellemeler).
    *   🚏 Durak eklemeleri ve çıkarmaları (Sıra numarasıyla birlikte).

### 2. � Hat Duyuruları
*   Ulaşım Dairesi tarafından yayınlanan anlık hat duyurularını takip eder.
*   Duyuru başlığı, içeriği ve geçerlilik tarih aralığını raporlar.

### 3. 💰 Fiyat Tarifesi Takibi
*   Hatların bilet fiyatlarındaki değişiklikleri izler.
*   Tam, Öğrenci ve İndirimli tarife değişimlerini anlık bildirir.

### 4. 📊 Açık Veri Portalı Entegrasyonu
*   Sakarya Büyükşehir Belediyesi [Açık Veri Portalı](https://veri.sakarya.bel.tr) üzerindeki "Ulaşım" veri setlerini izler.
*   Yeni eklenen veya güncellenen veri setlerini bildirir.

### 5. 📄 Diğer Özellikler
*   **UKOME Kararları:** Ulaşım Koordinasyon Merkezi kararlarının takibi.
*   **Haberler:** SBB Ulaşım haberlerinin takibi.
*   **Dökümanlar:** Yayınlanan yeni PDF dökümanlarının takibi.

---

## 🛠️ Teknik Altyapı

Proje **.NET 10 (C#)** ile geliştirilmiş olup, `Microsoft.Extensions.Hosting` (Worker Service) mimarisini kullanır.

### Servisler (Watcher Services)
Her servis `BackgroundService` olarak çalışır ve kendi sorumluluk alanındaki veriyi periyodik olarak kontrol eder:

| Servis | Görev | API Kaynağı |
| :--- | :--- | :--- |
| `BusLineWatcherService` | Hat listesi ve sefer saatlerini izler | `/api/v1/Ulasim?busType={...}` |
| `RouteWatcherService` | Güzergah ve durak detaylarını izler | `/api/v1/Ulasim/route-and-busstops/{id}` |
| `AnnouncementWatcherService` | Hat duyurularını izler | `/api/v1/Ulasim/announcement` |
| `FareWatcherService` | Fiyat tarifelerini izler | `/api/v1/Ulasim/line-fare/{id}` |
| `OpenDataWatcherService` | Açık Veri Portalı aktivitelerini izler | `veri.sakarya.bel.tr/api/3/...` |
| `UkomeWatcherService` | UKOME kararlarını izler | Web Scraping (HtmlAgilityPack) |

### Veri Kaynakları & API
Bot, veri çekmek için Sakarya Büyükşehir Belediyesi'nin Public API servislerini kullanır:
*   **Base URL:** `https://sbbpublicapi.sakarya.bel.tr`
*   **Open Data:** `https://veri.sakarya.bel.tr`

*Not: API istekleri `Origin` ve `Referer` başlıkları ile yetkilendirilmiştir.*

---

## ⚙️ Kurulum ve Yapılandırma

### Gereksinimler
*   .NET 8.0 veya üzeri SDK (Proje .NET 10 önizleme sürümü hedefli)
*   Telegram Bot Token

### Yapılandırma
`appsettings.json` dosyası üzerinden bot ayarları ve tarama sıklıkları yapılandırılabilir:

```json
{
  "Telegram": {
    "Token": "YOUR_BOT_TOKEN",
    "ChatId": "YOUR_CHANNEL_OR_GROUP_ID"
  },
  "Intervals": {
    "BusLineMinutes": 60,       // Hat listesi kontrolü
    "RouteMinutes": 1440,       // Güzergah/Durak kontrolü (Günlük)
    "FareMinutes": 1440,        // Fiyat kontrolü (Günlük)
    "AnnouncementMinutes": 10,  // Duyuru kontrolü
    "NewsMinutes": 30,          // Haber kontrolü
    "UkomeMinutes": 1440        // UKOME kontrolü
  }
}
```

### Çalıştırma

Terminal üzerinden projeyi derleyip çalıştırabilirsiniz:

```bash
cd sbb-bot
dotnet run
```

---

## � Proje Yapısı

```
sbb-bot/
├── Data/                 # JSON tabanlı yerel veritabanı (hatlar, hash'ler)
├── Helpers/              # Telegram ve Dosya işlemleri için yardımcı sınıflar
├── Models/               # API yanıtları için C# modelleri (RouteResponse vb.)
├── Services/             # Arka plan servisleri (Watcher'lar)
├── Program.cs            # Uygulama giriş noktası ve DI konteyneri
└── appsettings.json      # Konfigürasyon dosyası
```

---

## ⚠️ Yasal Uyarı
Bu proje Sakarya Büyükşehir Belediyesi'nin halka açık verilerini (`Public API`) ve `Açık Veri Portalı`nı kullanarak bilgilendirme amacı güder. Resmi bir uygulama değildir.

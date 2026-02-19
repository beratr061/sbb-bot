# 🚍 Sakarya Büyükşehir Belediyesi (SBB) Bot

SBB Bot, Sakarya Büyükşehir Belediyesi'nin resmi haberlerini, ihalelerini, meclis toplantılarını, UKOME kararlarını ve otobüs sefer saatlerini takip eden, kullanıcıları anlık olarak bilgilendiren kapsamlı bir **Telegram Botu**dur.

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat&logo=dotnet)](https://dotnet.microsoft.com/)
[![Telegram](https://img.shields.io/badge/Telegram-Bot-blue?style=flat&logo=telegram)](https://core.telegram.org/bots)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

---

## ✨ Özellikler

### 🚌 Otobüs Sefer Takibi
- Belediye ve Özel Halk Otobüslerinin sefer saatlerini otomatik takip eder.
- **Hafta İçi, Cumartesi ve Pazar** günlerine özel ayrı ayrı takip yapar.
- Sefer saatleri değiştiğinde **detaylı bildirim** gönderir:
    - ✅ Yeni eklenen seferler
    - ❌ Kaldırılan seferler
    - ⚠️ Haftasonu sefer iptalleri
- Sefer olmayan günler için kullanıcıyı bilgilendirir.

### 📰 Haber & Duyuru Takibi
- Belediyenin web sitesindeki son haberleri takip eder ve yeni bir haber yayınlandığında Telegram üzerinden paylaşır.

### 🏛️ Şeffaflık & Yönetim
- **Meclis Toplantıları:** Yaklaşan meclis toplantılarını ve gündemlerini bildirir.
- **UKOME Kararları:** Ulaşım ile ilgili alınan son UKOME kararlarını takip eder.
- **İhaleler:** Yayınlanan yeni ihaleleri kategorize ederek paylaşır.

---

## 🚀 Kurulum

Projeyi kendi ortamınızda çalıştırmak için aşağıdaki adımları izleyin.

### Gereksinimler
- [.NET 9.0 veya 10.0 SDK](https://dotnet.microsoft.com/download)
- Bir Telegram Bot Token'ı (BotFather üzerinden alabilirsiniz)

### Adım 1: Projeyi Klonlayın
```bash
git clone https://github.com/beratr061/sbb-bot.git
cd sbb-bot
```

### Adım 2: Konfigürasyon
`appsettings.json` dosyasını düzenleyin ve kendi Telegram Bot Token'ınızı ekleyin.

```json
{
  "BotConfig": {
    "TelegramBotToken": "YOUR_BOT_TOKEN_HERE",
    "AdminChatId": "YOUR_ADMIN_CHAT_ID",
    "ChannelId": "@sakaryaulasimduyuru",
    "Intervals": {
        "NewsMinutes": 15,
        "MeetingHours": 6,
        "BusLinesHours": 4
    }
  }
}
```

> ⚠️ **Not:** Güvenlik için `appsettings.json` dosyasını Git geçmişine yüklemeyin veya sensitive verileri environment variable (ortam değişkeni) olarak saklayın.

### Adım 3: Çalıştırın
```bash
dotnet run --project sbb-bot
```

---

## 🛠️ Teknik Detaylar

### Teknoloji Yığını
- **C# / .NET Core:** Performanslı ve modern backend.
- **HtmlAgilityPack:** Web kazıma (scraping) işlemleri için.
- **Polly:** HTTP isteklerinde retry (yeniden deneme) mekanizmaları için.
- **System.Text.Json & RegEx:** API yanıtlarını ve dinamik verileri işlemek için.

### Mimarisi
Proje, **BackgroundService** (Arka Plan Servisleri) mimarisi üzerine kuruludur. Her bir takip modülü (Haber, Otobüs, İhale vb.) birbirinden bağımsız bir servis olarak çalışır:

- `BusLineWatcherService`: Otobüs saatlerini izler.
- `NewsWatcherService`: Haberleri izler.
- `UkomeWatcherService`: UKOME kararlarını izler.
- `MeetingWatcherService`: Meclis gündemlerini izler.

---

## 🤝 Katkıda Bulunma
Katkılarınızı bekliyoruz! Bir sorun bulursanız issue açabilir veya bir özellik eklemek isterseniz Pull Request gönderebilirsiniz.

1. Forklayın
2. Feature branch oluşturun (`git checkout -b feature/YeniOzellik`)
3. Commitileyin (`git commit -m 'Yeni özellik eklendi'`)
4. Pushlayın (`git push origin feature/YeniOzellik`)
5. Pull Request açın

---

## 📜 Lisans
Bu proje [MIT Lisansı](LICENSE) ile lisanslanmıştır.

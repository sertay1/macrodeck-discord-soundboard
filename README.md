# <div align="center">Discord Soundboard for Macro Deck 3</div>

<p align="center">
  <img src="src/DiscordSoundboard/Assets/icon.svg" alt="Discord Soundboard Logo" width="128" height="128">
</p>

<p align="center">
  <strong>Trigger and control your Discord sounds seamlessly from your Macro Deck controller.</strong>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Macro%20Deck-%3E%3D3.0.0-5865F2?logo=discord" alt="Macro Deck 3">
  <img src="https://img.shields.io/badge/.NET-10.0-512bd4?logo=dotnet" alt=".NET 10.0">
  <img src="https://img.shields.io/badge/Platform-Windows-0078D6?logo=windows" alt="Windows">
  <img src="https://img.shields.io/badge/Version-v1.0.0-759a20" alt="Version 1.0.0">
  <img src="https://img.shields.io/badge/License-MIT-green" alt="License: MIT">
</p>

<div align="center">
  <a href="#en-setup-and-usage-guide" style="text-decoration:none;"><img src="https://img.shields.io/badge/Language-English-blue?style=for-the-badge" alt="English"></a> &nbsp;&nbsp; <a href="#tr-kurulum-ve-kullanım-rehberi" style="text-decoration:none;"><img src="https://img.shields.io/badge/Dil-Türkçe-red?style=for-the-badge" alt="Türkçe"></a>
</div>

---

<p align="center">
  <img src="docs/screenshots/01_discord_soundboard_showcase.png" alt="Discord Soundboard for Macro Deck Showcase" width="100%">
</p>

---

<a name="en-setup-and-usage-guide" id="en-setup-and-usage-guide"></a>
# 🇬🇧 English: Setup and Usage Guide

**Discord Soundboard** is a high-performance, native plugin for **Macro Deck 3**. It communicates directly with your running Discord desktop application to fetch all available soundboard sounds from your account and joined servers, letting you preview audio, organize favorites, and instantly export them as touch buttons on your Macro Deck.

---

## ✨ Features

- **🔒 Direct Discord IPC Connection:** No bots, bot tokens, or webhooks required. Connects directly to your local running Discord Desktop app via Discord's local RPC IPC protocol.
- **🔍 Automatic Soundboard Discovery:** Automatically fetches every sound available to you:
  - Default Discord sounds (Airhorn, Ba-Dum-Tss, Cricket, Golf Clap, Quack, etc.).
  - Custom sounds across all joined Discord servers (guilds).
  - Nitro-accessible cross-server soundboard audio.
- **🖥️ Built-in Interactive Soundboard Manager:**
  - Modern web-based visual management window running via Microsoft Edge WebView2.
  - Search sounds by name or server in real time.
  - Filter by specific server or browse your personal favorites.
  - **Live Audio Preview:** Test and listen to sound clips in real time before creating buttons.
  - **Favorites System:** Star your go-to sounds to keep them at the top.
- **⚡ 1-Click Macro Deck Export:** Select single or multiple sounds and export them directly into your current Macro Deck folder with custom emojis, colors, and auto-generated back buttons.
- **🌍 Dual-Language Support:** Fully localized in **English** 🇬🇧 and **Türkçe** 🇹🇷 with an instant language toggle.
- **📱 Hardware & Touch Compatible:** Works across Macro Deck Windows client, Android, iOS, and Web deck clients.

---

## 📋 Requirements

1. **Macro Deck 3.0.0 or later**
2. **Discord Desktop Application** (must be running on the same PC)
3. **Windows 10 / 11 (64-bit)**
4. **.NET 10.0 Runtime** (Windows Desktop x64)

---

## 🚀 Installation

### Option 1: Macro Deck Extension Store (Recommended ⭐)

1. Open **Macro Deck 3**.
2. Click the **Extension Store** icon on the sidebar.
3. Search for **`Discord Soundboard`**.
4. Click **Install**. Macro Deck will automatically download and activate the plugin.

---

### Option 2: Manual Package Installation (`.macroDeckPlugin`)

1. Go to the [Releases](https://github.com/sertay1/macrodeck-discord-soundboard/releases) page.
2. Download the latest **`com.sertaycil.discord-soundboard-1.0.0.macroDeckPlugin`**.
3. Double-click the downloaded file (or drag and drop it into Macro Deck).
4. Restart Macro Deck if prompted.

---

### Option 3: Build From Source

1. Clone this repository:
   ```bash
   git clone https://github.com/sertay1/macrodeck-discord-soundboard.git
   ```
2. Navigate to the project root and run the packaging script:
   ```powershell
   .\build-package.ps1
   ```
3. Deploy the compiled package located in `artifacts/` to your Macro Deck plugins folder:
   `%APPDATA%\MacroDeck\plugins\com.sertaycil.discord-soundboard\`

---

## 🎮 How to Use

1. **Launch Discord:** Ensure Discord Desktop is open and logged into your account.
2. **Open Soundboard Manager:** In Macro Deck, add the **"Open Soundboard Manager"** action to any button (or click it from the plugin settings).
3. **Filter & Select:** Search for sounds, preview audio, or filter by server.
4. **Export to Deck:** Choose your target deck folder and click **"Export Selected to Macro Deck"**.
5. **Blast Sounds in Voice Chat:** Join any Discord voice channel and tap your Macro Deck buttons to play sounds instantly!

---

## ⌨️ Controls & Actions

| Action | Description |
| :--- | :--- |
| **Play Discord Soundboard Sound** | Triggers a specific Discord soundboard sound by ID and Guild ID directly in your active voice channel. |
| **Open Soundboard Manager** | Opens the visual Soundboard Manager window to browse, preview, and batch-export sounds. |

---

## 🗑️ Uninstallation

To remove the plugin:
1. Open **Macro Deck** → **Extensions** tab.
2. Locate **Discord Soundboard** and click **Uninstall**.  
*(Or manually delete `%APPDATA%\MacroDeck\plugins\com.sertaycil.discord-soundboard`)*

---
---

<a name="tr-kurulum-ve-kullanım-rehberi" id="tr-kurulum-ve-kullanım-rehberi"></a>
# 🇹🇷 Türkçe: Kurulum ve Kullanım Rehberi

**Discord Soundboard**, **Macro Deck 3** için geliştirilmiş yüksek performanslı ve yerel bir eklentidir. Arka planda çalışan Discord masaüstü uygulamanızla doğrudan iletişim kurarak hesabınızdaki ve üye olduğunuz tüm sunuculardaki ses paneli (Soundboard) seslerini otomatik olarak çeker. Sesleri canlı dinlemenizi, favorilerinizi seçmenizi ve tek tıkla Macro Deck tuşlarınıza aktarmanızı sağlar.

---

## ✨ Özellikler

- **🔒 Doğrudan Discord IPC Bağlantısı:** Bot hesabı açmaya, bot tokenı veya webhook ile uğraşmaya gerek yoktur. Bilgisayarınızda açık olan Discord masaüstü uygulamasına doğrudan yerel RPC protokolüyle bağlanır.
- **🔍 Otomatik Soundboard Keşfi:** Hesabınızın erişebildiği tüm sesleri anında listeler:
  - Discord varsayılan sesleri (Korna, Ba-Dum-Tss, Cırcır Böceği, Alkış vb.).
  - Katıldığınız tüm Discord sunucularına (guild) ait özel sesler.
  - Nitro kullanıcıları için sunucular arası tüm sesler.
- **🖥️ Dahili İnteraktif Soundboard Yöneticisi:**
  - Modern ve şık Edge WebView2 arayüzü ile çalışır.
  - Ses adı veya sunucu adına göre anlık arama yapabilme.
  - Sunucuya göre filtreleme ve favoriler sekmesi.
  - **Canlı Ses Önizleme:** Buton oluşturmadan önce sesi tek tıkla canlı olarak dinleme.
  - **Favoriler Sistemi:** Sık kullandığınız sesleri yıldızlayarak her zaman elinizin altında tutma.
- **⚡ Tek Tıkla Macro Deck'e Aktarım:** Seçtiğiniz sesleri, özel emoji simgeleri, renkleri ve üst klasöre dönüş tuşlarıyla birlikte otomatik olarak Macro Deck butonlarına dönüştürür.
- **🌍 Çift Dil Desteği:** Dahili **Türkçe** 🇹🇷 ve **İngilizce** 🇬🇧 dil seçeneği (sağ üst menüden anında değiştirilebilir).
- **📱 Donanım ve Dokunmatik Uyumlu:** Macro Deck Windows, Android, iOS ve Web kumandalarıyla tam uyumlu çalışır.

---

## 📋 Sistem Gereksinimleri

1. **Macro Deck 3.0.0 veya daha yenisi**
2. **Discord Masaüstü Uygulaması** (aynı bilgisayarda açık olmalıdır)
3. **Windows 10 / 11 (64-bit)**
4. **.NET 10.0 Runtime** (Windows Desktop x64)

---

## 🚀 Kurulum Seçenekleri

### 1. Seçenek: Macro Deck Eklenti Mağazası (Tavsiye Edilen ⭐)

1. **Macro Deck 3** uygulamasını açın.
2. Sol menüdeki **Eklenti Mağazası (Extension Store)** simgesine tıklayın.
3. Arama kutusuna **`Discord Soundboard`** yazın.
4. **Yükle (Install)** butonuna basın. Eklenti otomatik olarak kurulacaktır.

---

### 2. Seçenek: Manuel Paket Kurulumu (`.macroDeckPlugin`)

1. [Releases](https://github.com/sertay1/macrodeck-discord-soundboard/releases) sayfasına gidin.
2. En güncel **`com.sertaycil.discord-soundboard-1.0.0.macroDeckPlugin`** dosyasını indirin.
3. İndirdiğiniz dosyaya çift tıklayın (veya Macro Deck penceresine sürükleyip bırakın).
4. İstenirse Macro Deck'i yeniden başlatın.

---

### 3. Seçenek: Kaynak Koddan Derleme

1. Depoyu klonlayın:
   ```bash
   git clone https://github.com/sertay1/macrodeck-discord-soundboard.git
   ```
2. Paketleme betiğini çalıştırın:
   ```powershell
   .\build-package.ps1
   ```
3. Derlenen dosyaları Macro Deck eklenti klasörünüze kopyalayın:
   `%APPDATA%\MacroDeck\plugins\com.sertaycil.discord-soundboard\`

---

## 🎮 Nasıl Kullanılır?

1. **Discord'u Açın:** Discord masaüstü uygulamanızın açık ve hesabınızın giriş yapmış olduğundan emin olun.
2. **Yöneticiyi Başlatın:** Macro Deck'te herhangi bir butona **"Open Soundboard Manager"** aksiyonunu atayın veya eklenti ayarlarından paneli açın.
3. **Filtreleyin ve Seçin:** İstediğiniz sesleri arayın, önizleme ile dinleyin ve aktarmak istediklerinizi işaretleyin.
4. **Macro Deck'e Aktarın:** Hedef klasörünüzü seçip **"Export Selected to Macro Deck"** butonuna basın.
5. **Ses Kanalında Çalın:** Herhangi bir Discord ses kanalındayken Macro Deck tuşlarınıza basarak sesleri anında sohbete gönderin!

---

## ⌨️ Eylemler (Actions)

| Eylem | Açıklama |
| :--- | :--- |
| **Play Discord Soundboard Sound** | Belirtilen Sound ID ve Guild ID'ye sahip sesi doğrudan aktif ses kanalınızda çalar. |
| **Open Soundboard Manager** | Sesleri aramak, filtrelemek, dinlemek ve topluca aktarmak için görsel yönetim panelini açar. |

---

## 🗑️ Kaldırma (Uninstall)

Eklentiyi kaldırmak için:
1. **Macro Deck** → **Eklentiler (Extensions)** sekmesine gidin.
2. **Discord Soundboard** eklentisini bulup **Kaldır (Uninstall)** butonuna basın.  
*(Veya `%APPDATA%\MacroDeck\plugins\com.sertaycil.discord-soundboard` klasörünü silin)*

---

## 📜 License / Lisans

This project is licensed under the [MIT License](LICENSE) - see the LICENSE file for details.  
Discord is a registered trademark of Discord Inc. This plugin is an independent community integration and is not officially affiliated with Discord Inc.

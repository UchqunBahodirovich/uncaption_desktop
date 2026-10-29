# Unicaption — native Windows ilova

Unicaption Flutter ilovasining (0.1.2+3) native Windows qayta yozilgan versiyasi:
**C# / .NET 8 + WinUI 3 (Windows App SDK 1.6)**, audio esa **WASAPI** (NAudio wrapper) orqali.
TZ bo‘yicha C# WinUI 3 varianti tanlangan (TZ §2: «jamoa C# tanlasa, UI WinUI 3’da, audio
qatlami C++ yoki mos Windows API wrapper’da»). Flutter, Electron yoki WebView ishlatilmaydi.

TZ’ning har bir bandi qayerda bajarilgani: [docs/TZ_MUVOFIQLIK.md](docs/TZ_MUVOFIQLIK.md).

## Tuzilma

```
Unicaption.sln
Directory.Build.props          ← yagona versiya (0.2.0)
src/
  Unicaption.Core/             ← platformadan mustaqil biznes-mantiq (net8.0, testlanadi)
    Api/          ApiClient (REST, timeout, retry, 401→refresh), JsonNormalizer, AccountService
    Auth/         AuthService: tizim browseri + PKCE + loopback (RFC 8252), Firebase token refresh
    Audio/        PCM format, StreamingResampler (→16 kHz mono), TimelineMixer (Both), BoundedAudioQueue
    Realtime/     RealtimeClient (WSS protokoli), TokenParser
    Session/      SessionController — holat mashinasi, bitta sessiya kafolati, usage bir marta
    Transcript/   TranscriptStore — segment/grouping/RTL qoidalari, UI’dan mustaqil so‘z hisobi
    Time/         QPC asosidagi monotonic clock, SessionClock
    Update/       Manifest, bounded download, SHA-256
    Settings/     WorkspaceSettings (theme, til, shrift, sidebar, context)
    Diagnostics/  Maxfiy ma’lumotsiz diagnostika
  Unicaption.App/              ← WinUI 3 ilova (Windows)
    Themes/       Light/Dark/HighContrast tokenlar, komponent uslublari
    Views/        Login, Playground
    Controls/     TranscriptPanel, NotesPanel, DictionaryPanel, ...
    Dialogs/      Language, Context, Support
    Platform/     WasapiAudioEngine, DpapiSecretStore, SystemBrowser, WindowHooks (min o‘lcham, suspend)
tests/Unicaption.Core.Tests    ← 61 ta unit/integratsiya testi (xUnit)
installer/                     ← Inno Setup skripti, build_installer.ps1, latest.json namunasi
```

## Toza Windows’da build

Talablar: Windows 10 1809+ yoki Windows 11 (x64), **.NET 8 SDK**, **Inno Setup 6**.
Visual Studio shart emas (debug uchun VS 2022 + «Windows application development» workload qulay).

```powershell
git clone <repo> unicaption-native
cd unicaption-native

# 1. Public konfiguratsiya (Google Desktop OAuth client id va havolalar)
copy src\Unicaption.App\unicaption.config.example.json src\Unicaption.App\unicaption.config.json
notepad src\Unicaption.App\unicaption.config.json      # docs/SETUP.md ga qarang

# 2. Testlar
dotnet test tests\Unicaption.Core.Tests

# 3. Ishga tushirish (debug)
dotnet run --project src\Unicaption.App -p:Platform=x64

# 4. Release: test → publish → installer → latest.json
powershell -ExecutionPolicy Bypass -File installer\build_installer.ps1
```

Natija: `installer\Output\UnicaptionSetup-<ver>.exe`, `UnicaptionSetup.exe`, `latest.json`.
Serverga avval installer, keyin `latest.json` yuklanadi.

Kod imzolash (ixtiyoriy): `SIGN_CERT_THUMBPRINT` muhit o‘zgaruvchisi berilsa `signtool` bilan exe va installer imzolanadi.

## Konfiguratsiya va maxfiylik

- `unicaption.config.json` faqat **public** identifikatorlarni saqlaydi (Google Desktop client id, Firebase web API key, havolalar). Server/payment secret client’ga kiritilmaydi.
- Refresh token **DPAPI (CurrentUser)** bilan shifrlangan holda `%LOCALAPPDATA%\Unicaption\session.dat` da.
- Oddiy afzalliklar: `%LOCALAPPDATA%\Unicaption\settings.json`.
- Diagnostika: `%LOCALAPPDATA%\Unicaption\diagnostics.log` — token, audio, transkript matni yo‘q; exception’ning faqat turi yoziladi.
- Audio va transkript diskka yozilmaydi.

## Muhim: nima tekshirildi, nima tekshirilmadi

- `Unicaption.Core` — macOS’da .NET 8 bilan build qilindi, **61/61 test o‘tdi**.
- `Unicaption.App` C# kodi haqiqiy WinUI 3 + NAudio referenslariga qarshi **xatosiz kompilyatsiya qilindi** (XAML generatsiya qiladigan qismlar stub qilingan holda).
- **XAML kompilyatsiyasi, ilovani ishga tushirish, real mikrofon/loopback, Google login va installer Windows’da hali sinovdan o‘tkazilmagan** — XAML kompilyatori faqat Windows’da ishlaydi. Birinchi qadam: Windows’da `dotnet build -p:Platform=x64` va `docs/TZ_MUVOFIQLIK.md` dagi QA ro‘yxati.

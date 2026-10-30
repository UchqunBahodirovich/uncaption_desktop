# TZ muvofiqligi (Unicaption native TZ v1.0)

Belgilar: **✅ bajarildi** · **🧪 unit-test bilan tasdiqlangan** · **🪟 Windows’da QA kutilmoqda** · **⏸ backend/mahsulot qarori kutilmoqda (TZ’ga ko‘ra ruxsat etilgan holatda)** · **❌ hali bajarilmagan**

## Tanlangan qarorlar

| Qaror | Tanlov | Sabab |
|---|---|---|
| Stack | C# .NET 8 + WinUI 3 (WinAppSDK 1.6, unpackaged, self-contained) | TZ §2 muqobil yo‘li; mantiq qatlami platformadan mustaqil va testlanadi |
| Audio | WASAPI shared capture + shared-mode loopback, NAudio wrapper | AUDIO 01 |
| Platforma | Windows x64 (ARM64 csproj’da yoqilgan, release’da tanlanadi), min. Windows 10 1809 | §2 |
| Migratsiya | Flutter ilova **almashtiriladi**: o‘sha AppId, o‘sha papka, eski fayllar o‘chiriladi; versiya 0.2.0 (> 0.1.2) | §17 — eski updater ham yangi ilovani yetkaza oladi |
| Install | Program Files, admin (hozirgidek) | §17 |
| Shrift | IBM Plex fayllari loyihada yo‘q → aniq fallback: Segoe UI Variable → Segoe UI (bitta resurs bilan almashtiriladi) | UI 01 ruxsat beradi |
| Dark `textMuted` | `#5A6578` → `#7D8899` (kontrast 4.5:1 uchun) | UI 05: token tuzatildi va qayd etildi |
| Sidebar «Context» popup | «Domain» deb nomlandi (ikkita bir xil «Context» bandini ajratish uchun) | Kichik UX aniqlik |

## Funksional bandlar

| TZ | Holat | Qayerda |
|---|---|---|
| AUTH 01 Google login tizim browserida, state+PKCE, embedded oyna yo‘q, Firebase ID token Bearer | ✅ 🪟 | `Core/Auth/AuthService.cs`, `Views/LoginPage` |
| AUTH 01 Cancel → login holatiga qaytish, crash yo‘q | ✅ 🪟 | `LoginPage.OnCancel` |
| AUTH 02 Token OS himoyasida (DPAPI), muddatidan oldin refresh, 401’da bitta refresh, imkonsiz bo‘lsa qayta login | ✅ 🧪 | `AuthService.GetTokenAsync`, `DpapiSecretStore`, `ApiClient.SendAsync` |
| AUTH 02 Log out sessiyani to‘xtatadi, hisobni tozalaydi, theme saqlanadi | ✅ | `MainWindow.SignOutAsync` |
| NAV 01 Profil kartasida Usage & Analytics va Billing & Plans | ➖ mahsulot qarori bilan olib tashlandi: kartada faqat tarif va qolgan daqiqalar | `PlaygroundPage.xaml` (mini rejimda tooltip) |
| NAV 02 Sessiya vaqtida til/model/manba bloklangan (Usage/Billing sahifalari yo‘q) | ✅ | `RenderSettings` |
| SESSION 01 7 holat, bitta start/capture/socket/clock, sessiya ID bilan kech callback’lar inkor | ✅ 🧪 | `Core/Session/SessionController.cs` |
| SESSION 02 Start/Stop ketma-ketligi, qisman resurslarni yopish, Stop’dan keyin matn qoladi | ✅ 🧪 | o‘sha |
| TIME 01 QPC/monotonic, UI tick faqat ko‘rsatadi, ekran va billing bir manbadan, Stop snapshot | ✅ 🧪 | `Core/Time/MonotonicClock.cs` |
| TIME 02 Suspend’da sessiya to‘xtaydi, uyqu billing’ga kirmaydi; network uzilsa Live tugaydi, usage bir marta | ✅ 🧪 🪟 | `WindowHooks` (PBT_APMSUSPEND), `OnSystemSuspendAsync` |
| AUDIO 01 Mic / System (loopback) / Both, default qurilmalar | ✅ 🪟 | `Platform/WasapiAudioEngine.cs` |
| AUDIO 02 PCM16 LE, 16 kHz, mono, binary frame, header yo‘q | ✅ 🧪 | `StreamingResampler`, `TimelineMixer` |
| AUDIO 03 Both — bitta timeline’da haqiqiy mix, clipping limit, sukunat to‘ldiriladi, 1 s ≈ 32 000 bayt | ✅ 🧪 | `TimelineMixer` |
| AUDIO 04 Ruxsat/qurilma yo‘q/uzilish/default almashishi xabarlari; Live faqat birinchi buffer’dan keyin; queue 2 s chegarasi | ✅ 🧪 🪟 | `WasapiAudioEngine`, `BoundedAudioQueue` |
| TEXT 01–02 Ikki panel, segment modeli, grouping qoidalari, 3000 ms monotonic, 50 segment | ✅ 🧪 | `Core/Transcript/TranscriptStore.cs` |
| TEXT 02 Partial semantikasi | ⏸ | Hozirgi kod kabi «snapshot» deb olingan; real payload bilan tasdiqlash kerak |
| TEXT 03 RTL to‘plami, yon chiziq o‘ngga, chap panelda manual toggle, Clear faqat chap panel | ✅ 🧪 🪟 | `TranscriptPanel` |
| TEXT 04 Matnni belgilash/copy, auto-scroll + «Latest» tugmasi | ✅ 🪟 | `TranscriptPanel` |
| WS 01 Bitta til, qidiruv, Confirm’da qo‘llanadi, target sarlavhasi «Select Translation Language» | ✅ | `Dialogs/LanguageDialog` |
| WS 02 Model/soha | ✅ / ⏸ | Domain: hammasi tanlanadi, Soniox’ga `context.general` orqali yuboriladi. Model: hamma Apex modellari tanlanadi (mahsulot qarori); consumer `model`ni o‘qimaydi, hozircha hammasi `stt-rt-v4` |
| WS 03 Context Save/Cancel ajratilgan, qayta ochilganda ko‘rinadi | ✅ | `Dialogs/ContextDialog` (lokal saqlanadi) |
| WS 03 Context’ni backend’ga uzatish, Community | ✅ / ➖ | Custom Context `context.terms` sifatida yuboriladi (jonli servisda tekshirilgan); translation prompt lokal; Community MVP’dan chiqarilgan |
| WS 04 Notes: copy/clear/close/drag/resize, 220–600 × 200–700, oynadan chiqmaydi, run davomida saqlanadi | ✅ 🪟 | `Controls/NotesPanel` |
| WS 05 Dictionary: translate endpoint, Enter yoki 300 ms debounce, loading/result/empty/error/retry, eski javob inkor | ✅ 🪟 | `Controls/DictionaryPanel` |
| WS 05 Support: faqat haqiqiy manzillar; Start Tour (onboarding qayta ochiladi); diagnostika nusxasi | ✅ | `Dialogs/SupportDialog`, `Controls/OnboardingTour` |
| ACCOUNT 01 Hisob holati loading/loaded/cached/error/retry, soxta Free/0 yo‘q (Usage sahifasi olib tashlandi) | ✅ 🧪 | `Core/Api/AccountService.cs`, profil kartasi |
| BILL 01–02 Billing sahifasi | ➖ mahsulot qarori bilan olib tashlandi | — |
| BILL 02 Checkout / top-up / karta | ⏸ | `price_id` va to‘lov shartnomasi kelgach; `ApiClient.CreateCheckoutSessionAsync` tayyor |
| HTTP 01 Timeout 10/30 s, GET bounded retry, POST/usage avtomatik qayta yuborilmaydi, raw body ko‘rsatilmaydi | ✅ 🧪 | `Core/Api/ApiClient.cs` |
| DATA 02 Yagona normalizatsiya, aliaslar, manfiy limit xato, «unlimited» ≠ 0, 0.55 formulasi yo‘q | ✅ 🧪 | `Core/Api/JsonNormalizer.cs` |
| DATA 03 Theme, tillar, shrift, sidebar, audio manba saqlanadi; token alohida | ✅ | `Core/Settings/WorkspaceSettings.cs` |
| DATA 04 So‘z soni UI’dan mustaqil, faqat final tokenlar | ✅ 🧪 | `TranscriptStore.SessionWordCount` |
| RT 01–02 auth → authenticated (10 s) → config → binary; ping 30 s; token parsing qoidalari. Haqiqiy backend `{"status":"authenticated"}` va `{"error":"…"}` (type’siz) yuboradi — ikkala shakl qabul qilinadi | ✅ 🧪 | `Core/Realtime/*` |
| RT 03 closed/error/timeout → SessionController; `soniox_disconnected` → config 3 marta | ✅ 🧪 | o‘sha |
| RT 03 Reconnect/resume | ⏸ | Server sequence/session shartnomasi kerak |
| UI 01–03 Tokenlar, tipografiya, geometriya (52/264/72/16/48/20…), 900 chegarasida mini sidebar, 180 ms easeOut | ✅ 🪟 | `Themes/*`, `PlaygroundPage` |
| UI 03 Min. oyna 1024×700, start 1280×720, PerMonitorV2 DPI | ✅ 🪟 | `MainWindow`, `WindowHooks`, `app.manifest` |
| UI 04 Holatlar (hover/pressed/focus/disabled), xato holatlari jadvali | ✅ 🪟 | `Themes/Styles.xaml`, `PlaygroundPage.ShowError` |
| UI 05 Klaviatura, Escape, fokus, accessibility name/tooltip, 32×32 hit area, screen reader status, reduced motion, high contrast | ✅ 🪟 | butun UI |
| UI 06 Barcha UI string’lar resurs fayllarida | ❌ | Matnlar hozircha XAML/C# ichida (inglizcha). Keyingi qadam: `.resw` ga ko‘chirish |
| UPDATE 01–03 Manifest validatsiyasi, umumiy+idle timeout, disk/uzilish, checksum mos kelmasa ishga tushmaydi, faol sessiyada avval Stop, silent install + qayta ochish | ✅ 🧪 🪟 | `Core/Update/UpdateService.cs`, `PlaygroundPage.InstallUpdateAsync`, `installer/*` |
| SEC 01 Secret client’da yo‘q, loglarda token/PCM/matn yo‘q | ✅ 🧪 | `Diagnostics`, `AppConfig` |
| DIAG 01 Lifecycle, xato kodi, audio format, nusxalanadigan xulosa | ✅ | `DiagnosticsLog`, Support → Copy diagnostics |

## Qabul sinovlari (AC)

| AC | Holat | Dalil |
|---|---|---|
| 01 Login/cancel | 🪟 | — |
| 02 Token expiry | 🧪 + 🪟 | `AuthServiceTests.ConcurrentRefresh_SingleFlight`, `RevokedRefresh_SignsOut`, `ApiClientTests.Unauthorized_RefreshesOnce` |
| 03 20 marta Start | 🧪 + 🪟 | `SessionControllerTests.TwentyClicks_CreateOneSession` |
| 04 Mic ruxsati rad | 🧪 + 🪟 | `PermissionDenied_NoFakeLive_RetryWorks` |
| 05 Mic 16 kHz mono PCM | 🧪 + 🪟 | `AudioTests.Resampler_*` |
| 06 System audio | 🪟 | — |
| 07 Both bir timeline | 🧪 + 🪟 | `Mixer_TwoSources_OneSecondIsOneSecond` |
| 08 Qurilma uzilishi | 🧪 + 🪟 | `DeviceLost_EndsSessionWithManagedError` |
| 09 Auth timeout | 🧪 | `AuthTimeout_CleansUp_NextStartPossible` |
| 10–12 Vaqt aniqligi, UI stall, OS soati | 🧪 + 🪟 | `SessionClockTests` (60 daqiqalik real test Windows’da) |
| 13 Stop’ni takror bosish | 🧪 | `RepeatedStop_OneCleanup_OneUsage_SnapshotSeconds` |
| 14–16 Partial/final, ikki speaker, 3 s gap | 🧪 | `TranscriptStoreTests` |
| 17 RTL/mixed | 🧪 + 🪟 | `DirectionChange_SplitsSegment` (bidi ko‘rinishi Windows’da) |
| 18–19 Clear, 50 segment | 🧪 | `ClearAndCap_DoNotAffectWordCount` |
| 20 Stop va yangi Start | 🧪 | `TextKeptAfterStop_ClearedOnNextStart` |
| 21–30 Dizayn, klaviatura, DPI | 🪟 | — |
| 31 Sleep/wake | 🧪 + 🪟 | `Suspend_StopsBeforeSleep` |
| 32 Network uzilishi | 🧪 | `NetworkDrop_EndsLive_UsageSentOnce` |
| 33–35 Update versiya, buzilgan manifest, checksum | 🧪 | `UpdateServiceTests` |
| 36–38 Download uzilishi, faol sessiyada update, clean install | 🪟 | — |
| 39 Secret tekshiruvi | 🧪 + 🪟 | `DiagnosticsTests`, `ErrorText_HidesBody` |
| 40 2 soatlik sessiya | 🪟 | bounded queue va mixer backlog cap mavjud |

## Ochiq qarorlar (TZ §21)

1. **Auth** — Google «Desktop app» client ID (docs/SETUP.md). Usiz login ishlamaydi.
2. **Backend** — PCM formatini config’da e’lon qilish kerakmi; partial token semantikasi; finish/session ID/usage idempotency.
3. **Tariflar** — `price_id`, haqiqiy narxlar, trial va entitlement mapping (hozirgi narxlar statik va «reference» deb belgilangan).
4. **Model** — consumer `model` maydonini o‘qimaydi; Apex Gen-2/2.5 uchun server tomonda tanlov kerak.
6. **Audio format** — consumer Soniox’ga `audio_format: auto` yuboradi, u sarlavhasiz PCM’ni tanimaydi. `customers.py`da klient yuborgan `pcm_s16le`/`sample_rate`/`num_channels`ni hurmat qilish kerak (patch tayyor, deploy qilinishi shart).
5. **Context** — prompt/domain transporti va saqlash scope’i.
6. **Tashqi havolalar** — Terms, Privacy, Calendly, WhatsApp URL’lari (berilsa konfiguratsiyaga yoziladi).
7. **Code signing** — sertifikat va imzolash siyosati (`build_installer.ps1` tayyor).

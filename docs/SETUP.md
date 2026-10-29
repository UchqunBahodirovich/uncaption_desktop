# OAuth, Firebase va muhit sozlamalari

## 1. Google OAuth «Desktop app» client (majburiy)

Hozirgi Flutter ilovadagi client (`…qbin0s2eot0k6ap7cp9pk0rnu471i4kc`) iOS/macOS turi; u Windows loopback redirect’ini qo‘llamaydi. Windows uchun alohida client kerak:

1. Google Cloud Console → `soniox-app` loyihasi → **APIs & Services → Credentials**.
2. **Create credentials → OAuth client ID → Application type: Desktop app**, nomi: `Unicaption Windows`.
3. Redirect URI qo‘shish shart emas: Desktop client `http://127.0.0.1:<port>` loopback’ni avtomatik qabul qiladi (RFC 8252).
4. `Client ID` ni `unicaption.config.json` dagi `GoogleDesktopClientId` ga yozing.
5. Google Desktop client uchun «client secret» beradi va uni installed app uchun maxfiy hisoblamaydi. Token almashuvi PKCE bilan secret’siz ishlamasa, `GoogleDesktopClientSecret` ga yozing. Bu qiymat server secret emas.

Oqim: tizim browseri → Google → `127.0.0.1` loopback (state + PKCE S256) → Google `id_token` → Firebase `accounts:signInWithIdp` → **Firebase ID token** (backend’ga Bearer) + Firebase refresh token (DPAPI’da).

## 2. Firebase

- Firebase Console → Authentication → Sign-in method → **Google** yoqilgan bo‘lishi kerak.
- Firebase Auth → **Settings → Authorized domains**: qo‘shimcha domen kerak emas (signInWithIdp REST).
- Google provider’ning «Web SDK configuration» bo‘limidagi *Whitelist client IDs from external projects* ro‘yxatiga yangi Desktop client ID qo‘shilishi kerak bo‘lishi mumkin (agar client boshqa loyihada yaratilsa).
- Default `FirebaseApiKey` — hozirgi desktop ilova ishlatayotgan kalit. Windows uchun alohida kalit chiqarilsa, konfiguratsiyada almashtiring. Kalitda API restriction bo‘lsa, *Identity Toolkit API* va *Token Service API* ruxsat etilgan bo‘lsin.

## 3. Tashqi havolalar

`TermsUrl`, `PrivacyUrl`, `BookCallUrl`, `WhatsAppUrl` bo‘sh qolsa, UI ularni ko‘rsatmaydi (UI 06). Real URL’lar berilganda konfiguratsiyaga yoziladi — kodni o‘zgartirish shart emas.

## 4. Update kanali

`UpdateManifestUrl` (default `https://unicaption.com/instals/latest.json`). Staging uchun boshqa URL qo‘yish mumkin.

## 5. Windows mikrofon ruxsati

Unpackaged desktop ilova uchun: **Settings → Privacy & security → Microphone → Let desktop apps access your microphone** yoqilgan bo‘lishi kerak. O‘chiq bo‘lsa, ilova aniq xabar va «Open Windows settings» tugmasini ko‘rsatadi.

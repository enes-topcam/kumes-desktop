# Kümes

CS2'deki evcil tavuğun Windows masaüstünde, görev çubuğunun üstünde 3D olarak yaşar. Yürür, eşelenir, uyur, pencerelerin üstüne tüner. Yemin ne zaman biteceğini de gösterir.

*Your CS2 pet chicken lives on your Windows desktop, in 3D, above the taskbar. The UI is in Turkish.*

![Kümes: tavuk pencerelerin üstünde](docs/kumes.gif)

## İndir

**[Kümes'in son sürümü (Windows, 64 bit)](https://github.com/enes-topcam/kumes-desktop/releases/latest)**

1. `Kumes-x.y.z-win-x64.zip` dosyasını indir ve bir klasöre çıkar.
2. `Kumes.exe`'yi çalıştır. .NET kurman gerekmez.
3. Steam'e giriş yap ya da "Giriş yapmadan dene"ye tıkla.

Windows "bilinmeyen yayımcı" uyarısı verirse "Ek bilgi" → "Yine de çalıştır" de. Exe imzalı değil.

**Gerekenler:** Windows 10 ya da 11 (64 bit) ve envanterinde bir CS2 tavuğu.

## Neler var

- Oyundaki tavuk modeli, rengi ve animasyonları
- Yem ve büyüme durumu, yem bitmeden bildirim
- Steam'e QR kod ya da kullanıcı adı + Steam Guard ile giriş
- Giriş yapmadan dene: Steam'e bağlanmadan örnek bir tavukla uygulamayı gör
- Birden fazla Steam hesabı ve koleksiyon
- CS2 açıkken tavuk kendiliğinden gizlenir
- Ekranın yenileme hızına göre akıcı çizim, az RAM (~25 MB)

![Ana sayfa](docs/ana-sayfa.png)

## Tavuk modeli nereden geliyor

Pakette Valve'a ait hiçbir dosya yok. İlk açılışta model, renkler ve animasyonlar senin bilgisayarında hazırlanır:

- **CS2 kuruluysa:** Dosyalar kendi CS2 kurulumundan çıkarılır (yaklaşık 1 dakika).
- **CS2 kurulu değilse:** Sadece tavuk dosyaları (~29 MB) senin Steam hesabınla Valve'ın içerik sunucusundan indirilir. Oyunun tamamı inmez.

İki durumda da dosyalar sadece bu bilgisayarda kalır (`%APPDATA%\KumesCS`).

## Hesabın

- Şifren yalnızca Steam'e gider, Kümes kaydetmez. Bu bilgisayarda sadece Steam'in verdiği oturum anahtarı, Windows'un kendi şifrelemesiyle saklanır.
- Kümes, Steam'e ayrı bir cihaz olarak bağlanır. Birkaç saniye "CS2 oynuyor" görünür ve envanterindeki tavuğu okur. Takas, satın alma, mesaj gibi hiçbir şey yapmaz. CS2'de oyundaysan araya girmez.
- Tamamen kaldırmak için: Steam → Ayarlar → Güvenlik → Yetkili cihazlar.

## Kaynaktan derleme

[.NET 10 SDK](https://dotnet.microsoft.com/download) gerekir.

```
dotnet build cs/Kumes -c Release
```

Exe `cs/Kumes/bin/Release/net10.0-windows/Kumes.exe` olarak çıkar. Çift tıklayıp açmak için `Kumes-baslat.vbs` de kullanılabilir.

CS2 kurulumundan model çıkarmak için [Source 2 Viewer](https://github.com/ValveResourceFormat/ValveResourceFormat) komut satırı aracı gerekir. `Source2Viewer-CLI.exe` ve yanındaki dosyaları exe'nin yanındaki `tools/vrf/` klasörüne koy (geliştirirken depo kökündeki `tools/vrf/` da olur). Bu araç yoksa ve Steam'e giriş yapılmışsa, tavuk dosyaları hesapla indirilir.

## Lisans

[MIT](LICENSE). Valve ve Steam ile resmi bir bağlantısı yoktur. Counter-Strike, CS2 ve Steam, Valve Corporation'ın ticari markalarıdır.

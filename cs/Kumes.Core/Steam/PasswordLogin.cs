// Kullanıcı adı + şifre ile giriş (mobil ve masaüstü). Şifre Steam'in açık anahtarıyla şifrelenip doğrudan Steam'e gider,
// hiçbir yerde saklanmaz; saklanan tek şey Steam'in verdiği oturum anahtarı (refresh token), QR girişindeki gibi.
//
// Steam, giriş yapılmamış bağlantıyı ~1 dakikada kapatıyor; kullanıcı Steam Guard kodunu e-postadan bulana kadar
// bağlantı kopabilir. Giriş oturumu Steam tarafında tutulduğu için (client_id + request_id) kodu göndermek ve
// sonucu sormak için gerekirse yeni bağlantı açılır.
using System.Security.Cryptography;
using System.Text;
using SteamKit2;
using SteamKit2.Internal;
using AuthService = SteamKit2.Internal.Authentication;

namespace Kumes.Steam;

public enum GuardCodeKind { Email, App }

// Steam Guard adımında arayüzün yapacakları
public interface ISteamGuardPrompt
{
	// Giriş isteği onay bekliyor: Steam uygulamasında (viaEmail=false) ya da e-postadaki bağlantıda (viaEmail=true).
	// canUseCode: hesap kodla girişe de izin veriyor ("Kod ile giriş yap" seçeneği gösterilebilir)
	void WaitingForAppApproval(bool canUseCode, bool viaEmail = false);
	// Onay beklerken kullanıcı kodla girmeyi seçince tamamlanır (seçmezse hiç tamamlanmaz)
	Task WhenCodeChosen();
	// Kod sor (e-posta ya da Steam uygulamasındaki kod). null: kullanıcı vazgeçti.
	Task<string?> AskCodeAsync(GuardCodeKind kind, string? email, bool previousWasWrong);
}

public partial class SteamService
{
	public async Task<SavedSession> PasswordLoginAsync(string username, string password, ISteamGuardPrompt guard, CancellationToken ct)
	{
		try { return await PasswordLoginCoreAsync(username, password, guard, ct); }
		finally { pending?.Dispose(); pending = null; }
	}

	async Task<SavedSession> PasswordLoginCoreAsync(string username, string password, ISteamGuardPrompt guard, CancellationToken ct)
	{
		// 1) şifreyi Steam'in açık anahtarıyla şifrele, giriş oturumunu başlat
		var key = await WithConnection(auth => auth.GetPasswordRSAPublicKey(
			new CAuthentication_GetPasswordRSAPublicKey_Request { account_name = username }).ToTask(), ct);
		if (key.Result is EResult.InvalidName or EResult.AccountNotFound or EResult.InvalidPassword)
			throw new SteamException("BAD_PASSWORD", "Kullanıcı adı ya da şifre yanlış.");
		if (key.Result != EResult.OK) throw new SteamException("STEAM_ERROR", $"Steam hatası: {key.Result}");

		string encrypted;
		using (var rsa = RSA.Create())
		{
			rsa.ImportParameters(new RSAParameters
			{
				Modulus = Convert.FromHexString(key.Body.publickey_mod),
				Exponent = Convert.FromHexString(key.Body.publickey_exp),
			});
			encrypted = Convert.ToBase64String(rsa.Encrypt(Encoding.UTF8.GetBytes(password), RSAEncryptionPadding.Pkcs1));
		}

		var r = await WithConnection(auth => auth.BeginAuthSessionViaCredentials(new CAuthentication_BeginAuthSessionViaCredentials_Request
		{
			account_name = username,
			encrypted_password = encrypted,
			encryption_timestamp = key.Body.timestamp,
			remember_login = true,
			persistence = ESessionPersistence.k_ESessionPersistence_Persistent,
			website_id = "Client",
			platform_type = EAuthTokenPlatformType.k_EAuthTokenPlatformType_SteamClient,
			device_friendly_name = deviceName,
			device_details = new CAuthentication_DeviceDetails
			{
				device_friendly_name = deviceName,
				platform_type = EAuthTokenPlatformType.k_EAuthTokenPlatformType_SteamClient,
				os_type = (int)(loginId == DesktopLoginId ? EOSType.Win11 : EOSType.AndroidUnknown),
			},
		}).ToTask(), ct);
		if (r.Result is EResult.InvalidPassword or EResult.InvalidName or EResult.AccountNotFound)
			throw new SteamException("BAD_PASSWORD", "Kullanıcı adı ya da şifre yanlış.");
		if (r.Result is EResult.RateLimitExceeded or EResult.AccountLoginDeniedThrottle)
			throw new SteamException("RATE_LIMIT", "Çok fazla deneme yapıldı, biraz bekleyip tekrar dene.");
		if (r.Result != EResult.OK) throw new SteamException("STEAM_ERROR", $"Steam hatası: {r.Result}");
		var begin = r.Body;

		ulong clientId = begin.client_id;
		var types = begin.allowed_confirmations.Select(c => c.confirmation_type).ToList();
		Log.Write($"giriş başladı, Steam Guard: {string.Join(", ", types)}");

		// 2) Steam Guard: uygulamadan onay varsa onu bekle (istenirse kodla da girilebilir); yoksa kod sor ve gönder
		var codeType = types.FirstOrDefault(t => t is EAuthSessionGuardType.k_EAuthSessionGuardType_EmailCode
			or EAuthSessionGuardType.k_EAuthSessionGuardType_DeviceCode);
		bool canCode = codeType != EAuthSessionGuardType.k_EAuthSessionGuardType_Unknown;

		async Task EnterCode()
		{
			var email = begin.allowed_confirmations.FirstOrDefault(c => c.confirmation_type == codeType)?.associated_message;
			var kind = codeType == EAuthSessionGuardType.k_EAuthSessionGuardType_EmailCode ? GuardCodeKind.Email : GuardCodeKind.App;
			for (bool wrong = false; ; wrong = true)
			{
				var code = await guard.AskCodeAsync(kind, email, wrong) ?? throw new OperationCanceledException();
				var res = await WithConnection(auth => auth.UpdateAuthSessionWithSteamGuardCode(new CAuthentication_UpdateAuthSessionWithSteamGuardCode_Request
				{
					client_id = clientId,
					steamid = begin.steamid,
					code = code.Trim().ToUpperInvariant(),
					code_type = codeType,
				}).ToTask(), ct);
				Log.Write($"Steam Guard kodu: {res.Result}");
				if (res.Result is EResult.OK or EResult.DuplicateRequest) return;
				if (res.Result is EResult.InvalidLoginAuthCode or EResult.TwoFactorCodeMismatch) continue;
				if (res.Result is EResult.Expired or EResult.FileNotFound)
					throw new SteamException("EXPIRED", "Giriş isteğinin süresi doldu, tekrar dene.");
				throw new SteamException("STEAM_ERROR", $"Kod kabul edilmedi: {res.Result}");
			}
		}

		Task? codeChosen = null;
		bool viaApp = types.Contains(EAuthSessionGuardType.k_EAuthSessionGuardType_DeviceConfirmation);
		if (viaApp || types.Contains(EAuthSessionGuardType.k_EAuthSessionGuardType_EmailConfirmation))
		{
			guard.WaitingForAppApproval(canCode, viaEmail: !viaApp); // e-posta bağlantısı: Steam tıklanınca girişi onaylar, sonucu aynı döngü alır
			if (canCode) codeChosen = guard.WhenCodeChosen();
		}
		else if (canCode) await EnterCode();

		// 3) sonucu sor: oturum anahtarı gelene kadar (bağlantı koparsa yenisiyle devam)
		// Steam'in önerdiği aralık genelde 5 sn; onay daha çabuk fark edilsin diye en fazla 3 sn
		var interval = TimeSpan.FromSeconds(Math.Clamp(begin.interval, 1, 3));
		var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
		while (DateTime.UtcNow < deadline)
		{
			ct.ThrowIfCancellationRequested();
			var poll = await WithConnection(auth => auth.PollAuthSessionStatus(new CAuthentication_PollAuthSessionStatus_Request
			{
				client_id = clientId,
				request_id = begin.request_id,
			}).ToTask(), ct);
			if (poll.Result is EResult.FileNotFound or EResult.Expired)
				throw new SteamException("EXPIRED", "Giriş isteğinin süresi doldu, tekrar dene.");
			if (poll.Result != EResult.OK) throw new SteamException("STEAM_ERROR", $"Steam hatası: {poll.Result}");
			if (poll.Body.new_client_id != 0) clientId = poll.Body.new_client_id;
			if (!string.IsNullOrEmpty(poll.Body.refresh_token))
			{
				var name = string.IsNullOrEmpty(poll.Body.account_name) ? username : poll.Body.account_name;
				var session = new SavedSession(name, poll.Body.refresh_token);
				store.Save(session);
				Log.Write($"şifreyle giriş tamam ({session.AccountName})");
				return session;
			}
			if (codeChosen == null) { await Task.Delay(interval, ct); continue; }
			// onay beklenirken kullanıcı "Kod ile giriş yap" derse kodu gönder, sonra sormaya devam
			var delay = Task.Delay(interval, ct);
			if (await Task.WhenAny(delay, codeChosen) == codeChosen) { codeChosen = null; await EnterCode(); }
			else await delay;
		}
		throw new SteamException("EXPIRED", "Giriş onaylanmadı (zaman aşımı).");
	}

	// Giriş adımları arasında açık tutulan bağlantı; Steam kapattıysa yenisi açılır
	Connection? pending;

	async Task<SteamUnifiedMessages.ServiceMethodResponse<T>> WithConnection<T>(
		Func<AuthService, Task<SteamUnifiedMessages.ServiceMethodResponse<T>>> call, CancellationToken ct)
		where T : ProtoBuf.IExtensible, new()
	{
		for (int attempt = 1; ; attempt++)
		{
			try
			{
				if (pending == null || !pending.Client.IsConnected)
				{
					pending?.Dispose();
					pending = new Connection { Closing = true }; // kopması beklenen durum, "bağlantı koptu" kaydı yazma
					await pending.ConnectAsync(ct);
				}
				var service = pending.Client.GetHandler<SteamUnifiedMessages>()!.CreateService<AuthService>();
				return await call(service).WaitAsync(TimeSpan.FromSeconds(20), ct);
			}
			catch (Exception e) when (e is not OperationCanceledException && attempt < 3)
			{
				Log.Write($"giriş bağlantısı yenileniyor: {e.Message}");
				pending?.Dispose();
				pending = null;
			}
		}
	}
}

// Steam + CS2 Game Coordinator bağlantısı (SteamKit2).
// Her güncellemede: bağlan -> giriş -> "CS2 oynuyor" -> GC hello -> welcome içindeki envanter -> çıkış.
using System.Text.Json;
using Kumes.Steam.Proto;
using ProtoBuf;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.GC;
using SteamKit2.Internal;
using GcHello = Kumes.Steam.Proto.CMsgClientHello;

namespace Kumes.Steam;

public record SteamProfile(string? PersonaName, string? AvatarHash, ulong SteamId64);
public record FetchResult(PetInfo Pet, SteamProfile Profile);

public class SteamException(string code, string message) : Exception(message)
{
	public string Code { get; } = code;
}

// deviceName: Steam'in "giriş yapılan cihazlar" listesinde görünen ad.
// loginId: aynı ağdaki Steam oturumları birbirini atmasın diye her istemciye ayrı (masaüstü "KMSC", mobil başka).
public partial class SteamService(ISessionStore store, string deviceName, uint loginId)
{
	const uint Cs2AppId = 730;
	public const uint DesktopLoginId = 0x4B4D5343; // "KMSC"
	public const uint MobileLoginId = 0x4B4D534D;  // "KMSM"

	// Steam sunucusuna bağlantı türü (null: SteamKit2'nin varsayılanı). Android'de WebSocket bağlanamıyor
	// (Unable to connect), mobil TCP kullanır.
	public static ProtocolTypes? Protocols;
	static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

	// ---------- QR ile giriş ----------
	public async Task<SavedSession> QrLoginAsync(Action<string> onQr, Action? onScanned, CancellationToken ct)
	{
		// Steam giriş yapılmamış bağlantıyı bir süre sonra kapatıyor; her QR oturumu için yeni bağlantı aç.
		for (int attempt = 1; ; attempt++)
		{
			ct.ThrowIfCancellationRequested();
			using var conn = new Connection();
			try { await conn.ConnectAsync(ct); }
			catch (Exception e) when (e is not OperationCanceledException && attempt < 10)
			{
				Log.Write($"bağlanılamadı, tekrar denenecek: {e.Message}");
				await Task.Delay(3000, ct);
				continue;
			}
			var auth = await conn.Client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
			{
				DeviceFriendlyName = deviceName,
				IsPersistentSession = true,
			});
			// Steam QR'ı ~20 sn'de bir değiştirir; yeni adres gelince ekrandakini yenile.
			auth.ChallengeURLChanged = () =>
			{
				Log.Write("QR yenilendi");
				onQr(auth.ChallengeURL);
			};
			onQr(auth.ChallengeURL);

			try
			{
				var result = await auth.PollingWaitForResultAsync(ct);
				Log.Write($"QR onaylandı ({result.AccountName})");
				var session = new SavedSession(result.AccountName, result.RefreshToken);
				store.Save(session);
				return session;
			}
			catch (Exception e) when (e is not OperationCanceledException)
			{
				// Süresi dolan QR oturumu: yenisini başlat
				Log.Write($"QR oturumu bitti, yenisi açılıyor: {e.Message}");
			}
		}
	}

	// ---------- Tavuk verisini çek ----------
	// Tavuk verisi + hesabın Steam'deki görünen adı ve profil fotoğrafı (pencere için)
	public async Task<FetchResult> FetchPetAsync(CancellationToken ct)
	{
		var saved = store.Load() ?? throw new SteamException("NEED_LOGIN", Lang.T("Steam girişi gerekiyor."));

		using var conn = new Connection();
		var user = conn.Client.GetHandler<SteamUser>()!;
		var gc = conn.Client.GetHandler<SteamGameCoordinator>()!;
		var friends = conn.Client.GetHandler<SteamFriends>()!;
		var result = new TaskCompletionSource<FetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		string? persona = null, avatarHash = null;
		SteamID? me = null;
		PetInfo? pet = null;
		DateTime petAt = DateTime.MaxValue;
		bool inGame = false, sessionState = false, blocked = false;
		DateTime loggedOnAt = DateTime.MaxValue;
		DateTime nextHello = DateTime.MaxValue;
		TimeSpan helloDelay = TimeSpan.FromSeconds(1);

		conn.Manager.Subscribe<SteamUser.LoggedOnCallback>(cb =>
		{
			if (cb.Result != EResult.OK)
			{
				Log.Write($"giriş reddedildi: {cb.Result} / {cb.ExtendedResult}");
				if (cb.Result is EResult.InvalidPassword or EResult.AccessDenied or EResult.Expired or EResult.Revoked or EResult.InvalidSignature)
				{
					store.Clear($"steam reddetti ({cb.Result})");
					result.TrySetException(new SteamException("NEED_LOGIN", Lang.T("Steam oturumunun süresi dolmuş, tekrar giriş yap.")));
				}
				else if (cb.Result is EResult.RateLimitExceeded or EResult.AccountLoginDeniedThrottle)
					result.TrySetException(new SteamException("RATE_LIMIT", Lang.T("Steam çok fazla deneme yapıldığını söylüyor.")));
				else
					result.TrySetException(new SteamException("STEAM_ERROR", Lang.Tf("Steam hatası: {0}", cb.Result)));
				return;
			}

			Log.Write("steam girişi tamam");
			_ = MaybeRenewAsync(conn.Client, cb.ClientSteamID!, saved);
			me = cb.ClientSteamID;
			friends.RequestFriendInfo(cb.ClientSteamID!, EClientPersonaStateFlag.PlayerName | EClientPersonaStateFlag.Presence); // ad + profil fotoğrafı
			loggedOnAt = DateTime.UtcNow;
			// "CS2 oynuyor" demeden önce Steam'in "bu hesap başka yerde oyunda mı?" cevabını bekle (OnTick):
			// başka bir bilgisayarda oyun açıkken doğrudan oyuna girmek oradaki oyunu düşürebilir.
		});

		conn.Manager.Subscribe<SteamUser.PlayingSessionStateCallback>(cb =>
		{
			sessionState = true;
			if (!cb.PlayingBlocked) return;
			blocked = true;
			Log.Write($"hesap başka bir yerde oyunda (uygulama {cb.PlayingAppID}), CS2'ye girilmedi");
			result.TrySetException(new SteamException("PLAYING_ELSEWHERE", cb.PlayingAppID == Cs2AppId
				? Lang.T("CS2 başka bir yerde açık; kapanınca güncellenecek.")
				: Lang.T("Steam'de başka bir oyun açık; kapanınca güncellenecek.")));
		});

		conn.Manager.Subscribe<SteamUser.AccountInfoCallback>(cb => persona = cb.PersonaName);
		conn.Manager.Subscribe<SteamFriends.PersonaStateCallback>(cb =>
		{
			if (me == null || cb.FriendID != me) return;
			if (!string.IsNullOrEmpty(cb.Name)) persona = cb.Name;
			if (cb.AvatarHash is { Length: > 0 } h && h.Any(b => b != 0)) avatarHash = Convert.ToHexString(h).ToLowerInvariant();
		});

		conn.Manager.Subscribe<SteamUser.LoggedOffCallback>(cb =>
			result.TrySetException(new SteamException("STEAM_ERROR", Lang.Tf("Steam oturumu kapattı: {0}", cb.Result))));

		conn.Manager.Subscribe<SteamGameCoordinator.MessageCallback>(cb =>
		{
			if (cb.AppID != Cs2AppId || cb.EMsg != GcMsg.ClientWelcome) return;
			try
			{
				var welcome = new ClientGCMsgProtobuf<CMsgClientWelcome>(cb.Message).Body;
				var items = new List<CSOEconItem>();
				foreach (var cache in welcome.outofdate_subscribed_caches)
					foreach (var type in cache.objects.Where(o => o.type_id == GcMsg.SoTypeEconItem))
						foreach (var data in type.object_data)
							items.Add(Serializer.Deserialize<CSOEconItem>(new MemoryStream(data)));
				Log.Write($"CS2 sunucusu: {items.Count} eşya alındı");
				pet = PetParser.Parse(items);
				petAt = DateTime.UtcNow; // profil fotoğrafı henüz gelmediyse kısa bir süre bekle (OnTick)
			}
			catch (Exception e)
			{
				result.TrySetException(new SteamException("PARSE_ERROR", Lang.Tf("Veri okunamadı: {0}", e.Message)));
			}
		});

		conn.OnDisconnected = () => result.TrySetException(new SteamException("STEAM_ERROR", Lang.T("Steam bağlantısı koptu.")));
		conn.OnTick = () =>
		{
			// tavuk verisi geldi: profil bilgisi de geldiyse (ya da 1,5 sn geçtiyse) bitir
			if (pet != null && (avatarHash != null || DateTime.UtcNow - petAt > TimeSpan.FromSeconds(1.5)))
				result.TrySetResult(new FetchResult(pet, new SteamProfile(persona, avatarHash, me?.ConvertToUInt64() ?? 0)));
			// oyun durumu geldi (başka yerde oynanmıyor) ya da Steam 5 sn'dir sessiz: CS2'ye gir
			if (!inGame && !blocked && loggedOnAt != DateTime.MaxValue && (sessionState || DateTime.UtcNow - loggedOnAt > TimeSpan.FromSeconds(5)))
			{
				if (!sessionState) Log.Write("oyun durumu bilgisi gelmedi, devam ediliyor");
				var play = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
				play.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed { game_id = new GameID(Cs2AppId) });
				conn.Client.Send(play);
				inGame = true;
				nextHello = DateTime.UtcNow.AddSeconds(1);
			}
			// GC'ye "merhaba" de; cevap gelene kadar artan aralıklarla tekrarla (globaloffensive ile aynı)
			if (!inGame || DateTime.UtcNow < nextHello) return;
			var hello = new ClientGCMsgProtobuf<GcHello>(GcMsg.ClientHello);
			hello.Body.version = 2000244;
			gc.Send(hello, Cs2AppId);
			nextHello = DateTime.UtcNow + helloDelay;
			helloDelay = TimeSpan.FromSeconds(Math.Min(60, helloDelay.TotalSeconds * 2));
		};

		await conn.ConnectAsync(ct);
		user.LogOn(new SteamUser.LogOnDetails
		{
			Username = saved.AccountName,
			AccessToken = saved.RefreshToken,
			ShouldRememberPassword = true,
			MachineName = deviceName,
			LoginID = loginId,
		});

		try
		{
			return await result.Task.WaitAsync(Timeout, ct);
		}
		catch (TimeoutException)
		{
			throw new SteamException("TIMEOUT", Lang.T("CS2 sunucusuna bağlanılamadı (zaman aşımı)."));
		}
		finally
		{
			conn.Closing = true; // çıkışın getirdiği kopma hata sayılmasın
			if (inGame)
			{
				var stop = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
				conn.Client.Send(stop);
			}
			user.LogOff();
			await Task.Delay(500, CancellationToken.None);
		}
	}

	// Süresi 30 günden az kaldıysa yenile. Yeni anahtar kaydedilmeden eskisi bırakılmaz.
	async Task MaybeRenewAsync(SteamClient client, SteamID steamId, SavedSession saved)
	{
		var exp = Jwt.Expiry(saved.RefreshToken);
		if (exp == null || exp - DateTimeOffset.UtcNow > TimeSpan.FromDays(30)) return;
		try
		{
			var r = await client.Authentication.GenerateAccessTokenForAppAsync(steamId, saved.RefreshToken, allowRenewal: true);
			if (!string.IsNullOrEmpty(r.RefreshToken) && r.RefreshToken != saved.RefreshToken)
			{
				store.Save(saved with { RefreshToken = r.RefreshToken });
				Log.Write("oturum yenilendi");
			}
		}
		catch (Exception e)
		{
			Log.Write($"oturum yenilenemedi (eskisiyle devam): {e.Message}");
		}
	}

	// SteamClient + CallbackManager'ı ayrı bir iş parçacığında döndürür.
	sealed class Connection : IDisposable
	{
		public SteamClient Client { get; } = new(SteamConfiguration.Create(c =>
		{
			c.WithConnectionTimeout(TimeSpan.FromSeconds(20));
			if (Protocols is { } p) c.WithProtocolTypes(p);
		}));
		public CallbackManager Manager { get; }
		public Action? OnDisconnected;
		public Action? OnTick;
		readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
		volatile bool running = true;
		public volatile bool Closing;
		readonly Thread thread;

		public Connection()
		{
			Manager = new CallbackManager(Client);
			Manager.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult());
			Manager.Subscribe<SteamClient.DisconnectedCallback>(cb =>
			{
				connected.TrySetException(new SteamException("STEAM_ERROR", Lang.T("Steam'e bağlanılamadı.")));
				if (Closing) return;
				Log.Write("steam bağlantısı koptu");
				OnDisconnected?.Invoke();
			});
			thread = new Thread(() =>
			{
				while (running)
				{
					Manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(200));
					OnTick?.Invoke();
				}
			}) { IsBackground = true, Name = "steam-callbacks" };
			thread.Start();
		}

		public async Task ConnectAsync(CancellationToken ct)
		{
			Client.Connect();
			await connected.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
		}

		public void Dispose()
		{
			Closing = true;
			Client.Disconnect();
			running = false;
			thread.Join(2000);
		}
	}
}

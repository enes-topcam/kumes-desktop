// Geliştirme: giriş penceresini Steam'e bağlanmadan dener (Kumes.exe --login-test).
// Şifre gönderilince sahte Steam Guard adımları oynar: önce uygulama onayı ("Kod ile giriş yap" ile koda geçilir),
// kod "AAAAA" ise yanlış sayılır, başka kod girişi tamamlar. Hiçbir şey Steam'e gitmez, kaydedilmez.
using System.Collections.Concurrent;
using Kumes.Steam;
using Kumes.Ui;
using static Kumes.Native.Win32;

namespace Kumes;

static class LoginTest
{
	public static void Run()
	{
		var queue = new ConcurrentQueue<Action>();
		void Post(Action a) => queue.Enqueue(a);
		var w = new LoginWindow();
		bool open = true;
		w.Closed = () => open = false;
		w.SetQr("https://s.team/q/1/0000000000000000000");
		CancellationTokenSource? cts = null;
		w.PasswordCancel = () => cts?.Cancel();
		w.PasswordLogin = (user, pass) =>
		{
			cts = new CancellationTokenSource();
			var guard = w.CreateGuard(Post);
			var ct = cts.Token;
			Task.Run(async () =>
			{
				await Task.Delay(800, ct);
				if (user == "yanlis") throw new SteamException("BAD_PASSWORD", "Kullanıcı adı ya da şifre yanlış.");
				guard.WaitingForAppApproval(canUseCode: true);
				await Task.WhenAny(guard.WhenCodeChosen(), Task.Delay(Timeout.Infinite, ct));
				ct.ThrowIfCancellationRequested();
				for (bool wrong = false; ; wrong = true)
				{
					var code = await guard.AskCodeAsync(GuardCodeKind.Email, null, wrong) ?? throw new OperationCanceledException();
					await Task.Delay(600, ct);
					if (code.Trim().ToUpperInvariant() != "AAAAA") break;
				}
			}, ct).ContinueWith(t => Post(() =>
			{
				if (t.IsCompletedSuccessfully) w.SetDone("Giriş tamam! Tavuğun aranıyor…");
				else w.PasswordFailed(t.Exception?.InnerException is SteamException e ? e.Message : null);
			}));
		};
		w.ShowAndFocus();
		while (open)
		{
			while (PeekMessageW(out var m, 0, 0, 0, PM_REMOVE))
			{
				if (w.PreTranslate(m)) continue;
				TranslateMessage(m);
				DispatchMessageW(m);
			}
			while (queue.TryDequeue(out var a)) a();
			Thread.Sleep(10);
		}
	}
}

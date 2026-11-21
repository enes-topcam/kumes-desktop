// Masaüstü: Steam oturumu (hesap adı + refresh token) Windows DPAPI ile şifrelenip saklanır.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kumes.Steam;

class TokenStore(string path) : ISessionStore
{
	static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Kumes.SteamSession.v1");

	public bool Exists => File.Exists(path);

	public SavedSession? Load()
	{
		try
		{
			var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
			return JsonSerializer.Deserialize<SavedSession>(plain);
		}
		catch (Exception e)
		{
			Log.Write($"oturum okunamadı: {e.Message}");
			return null;
		}
	}

	public void Save(SavedSession s)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var data = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(s), Entropy, DataProtectionScope.CurrentUser);
		var tmp = path + ".tmp";
		File.WriteAllBytes(tmp, data);
		File.Move(tmp, path, overwrite: true);
		Log.Write("oturum kaydedildi");
	}

	public void Clear(string reason)
	{
		if (!File.Exists(path)) return;
		Log.Write($"oturum SİLİNDİ, sebep: {reason}");
		File.Delete(path);
	}
}

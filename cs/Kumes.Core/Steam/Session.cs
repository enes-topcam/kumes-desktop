// Steam oturumu (hesap adı + refresh token) ve onu saklayan yer. Saklama platforma özel:
// masaüstünde Windows DPAPI, mobilde Android Keystore / iOS Keychain.
using System.Text.Json;

namespace Kumes.Steam;

public record SavedSession(string AccountName, string RefreshToken);

public interface ISessionStore
{
	bool Exists { get; }
	SavedSession? Load();
	void Save(SavedSession s);
	void Clear(string reason);
}

public static class Jwt
{
	// JWT'nin bitiş tarihi (exp)
	public static DateTimeOffset? Expiry(string jwt)
	{
		try
		{
			var part = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
			part = part.PadRight(part.Length + (4 - part.Length % 4) % 4, '=');
			using var doc = JsonDocument.Parse(Convert.FromBase64String(part));
			return doc.RootElement.TryGetProperty("exp", out var exp) ? DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()) : null;
		}
		catch { return null; }
	}
}

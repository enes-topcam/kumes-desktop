// CS2 Game Coordinator'dan envanteri okumak için gereken mesajlar.
// Alan numaraları node_modules/globaloffensive/protobufs (gcsdk_gcmessages / base_gcmessages) ile aynı.
using ProtoBuf;

namespace Kumes.Steam.Proto;

public static class GcMsg
{
	public const uint ClientWelcome = 4004;
	public const uint ClientHello = 4006;
	public const uint ClientConnectionStatus = 4009;
	public const int SoTypeEconItem = 1;
}

[ProtoContract]
public class CMsgClientHello : Extensible
{
	[ProtoMember(1)] public uint version { get; set; }
	[ProtoMember(3)] public uint client_session_need { get; set; }
	[ProtoMember(4)] public uint client_launcher { get; set; }
}

[ProtoContract]
public class CMsgClientWelcome : Extensible
{
	[ProtoMember(1)] public uint version { get; set; }
	[ProtoMember(3)] public List<CMsgSOCacheSubscribed> outofdate_subscribed_caches { get; } = new();
}

[ProtoContract]
public class CMsgSOCacheSubscribed : Extensible
{
	[ProtoContract]
	public class SubscribedType : Extensible
	{
		[ProtoMember(1)] public int type_id { get; set; }
		[ProtoMember(2)] public List<byte[]> object_data { get; } = new();
	}

	[ProtoMember(2)] public List<SubscribedType> objects { get; } = new();
}

[ProtoContract]
public class CSOEconItemAttribute : Extensible
{
	[ProtoMember(1)] public uint def_index { get; set; }
	[ProtoMember(2)] public uint value { get; set; }
	[ProtoMember(3)] public byte[]? value_bytes { get; set; }
}

[ProtoContract]
public class CSOEconItem : Extensible
{
	[ProtoMember(1)] public ulong id { get; set; }
	[ProtoMember(4)] public uint def_index { get; set; }
	[ProtoMember(10)] public string? custom_name { get; set; }
	[ProtoMember(12)] public List<CSOEconItemAttribute> attribute { get; } = new();
	[ProtoMember(15)] public uint style { get; set; }
}

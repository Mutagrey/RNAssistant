using System;
using System.IO;

namespace RNAssistant.OfficeHosts.Qualification
{
    // Diagnostic candidate only. Never accepts or unmarshals a packet from disk/network.
    public sealed class ComIdentitySample
    {
        public const int MaximumPacketBytes = 65536;
        internal static readonly Guid UnknownInterface = new Guid("00000000-0000-0000-C000-000000000046");

        public string Oxid { get; private set; }
        public string Oid { get; private set; }
        public string Ipid { get; private set; }
        public string Candidate { get { return Oxid + ":" + Oid; } }

        internal static ComIdentitySample Parse(byte[] packet)
        {
            if (packet == null || packet.Length < 76 || packet.Length > MaximumPacketBytes)
                throw new InvalidDataException("OBJREF packet size is outside the probe bounds.");
            using (var reader = new BinaryReader(new MemoryStream(packet, false)))
            {
                if (reader.ReadUInt32() != 0x574f454d)
                    throw new InvalidDataException("OBJREF signature is invalid.");
                var format = reader.ReadUInt32();
                if (format != 1)
                    throw new InvalidDataException("Unsupported OBJREF format 0x" + format.ToString("x8") +
                        "; no identity fallback is permitted.");
                if (new Guid(reader.ReadBytes(16)) != UnknownInterface)
                    throw new InvalidDataException("Probe expected a marshaled IUnknown.");
                reader.ReadUInt32(); // STDOBJREF flags, not object identity.
                reader.ReadUInt32(); // Public references, released with the original packet.
                var oxid = reader.ReadUInt64();
                var oid = reader.ReadUInt64();
                var ipid = new Guid(reader.ReadBytes(16));
                var entries = reader.ReadUInt16();
                var securityOffset = reader.ReadUInt16();
                if (entries < 4 || securityOffset < 2 || securityOffset > entries - 2 ||
                    packet.Length != 68 + entries * 2 ||
                    packet[68 + (securityOffset - 1) * 2] != 0 || packet[69 + (securityOffset - 1) * 2] != 0 ||
                    packet[packet.Length - 2] != 0 || packet[packet.Length - 1] != 0)
                    throw new InvalidDataException("OBJREF resolver array is incomplete or malformed.");
                if (oxid == 0 || oid == 0 || ipid == Guid.Empty)
                    throw new InvalidDataException("Probe refuses an empty COM identity.");
                return new ComIdentitySample
                {
                    Oxid = oxid.ToString("x16"), Oid = oid.ToString("x16"), Ipid = ipid.ToString("D")
                };
            }
        }
    }
}

using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;
using PortStrider.Core.Models;

namespace PortStrider.Infrastructure.Probes;

internal sealed record DnsResponse(int Rcode, bool Truncated, IReadOnlyList<DnsAnswer> Answers);

/// <summary>DNS encoding and bounded parsing. Never accepts a partially decoded answer.</summary>
internal static class DnsWire
{
    public static string NormalizeName(string name)
    {
        var normalized = name.Trim().TrimEnd('.');
        var labels = normalized.Split('.');
        if (labels.Any(string.IsNullOrEmpty)) throw new ArgumentException("DNS name contains an empty label.");
        var ascii = string.Join('.', labels.Select(label => new IdnMapping().GetAscii(label)));
        if (ascii.Length > 253 || ascii.Split('.').Any(label => label.Length > 63))
            throw new ArgumentException("DNS name exceeds the label or name length limit.");
        return ascii.ToLowerInvariant();
    }

    public static byte[] BuildAQuery(ushort id, string name) => BuildQuery(id, name, DnsRecordType.A);

    public static byte[] BuildQuery(ushort id, string name, DnsRecordType type)
    {
        if (!Enum.IsDefined(type)) throw new ArgumentException("Unsupported DNS record type.");
        var buffer = new List<byte> { (byte)(id >> 8), (byte)id, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in NormalizeName(name).Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            buffer.Add((byte)bytes.Length);
            buffer.AddRange(bytes);
        }
        buffer.AddRange([0, (byte)((ushort)type >> 8), (byte)type, 0, 1]);
        return buffer.ToArray();
    }

    public static bool TryParseResponse(byte[] data, ushort id, string name, DnsRecordType type, out DnsResponse? response)
    {
        response = null;
        if (data.Length < 12 || U16(data, 0) != id || (data[2] & 0xF8) != 0x80 || U16(data, 4) != 1) return false;
        var offset = 12;
        if (!ReadName(data, ref offset, out var question) || offset + 4 > data.Length) return false;
        if (!question.Equals(name, StringComparison.OrdinalIgnoreCase) || U16(data, offset) != (ushort)type || U16(data, offset + 2) != 1) return false;
        offset += 4;
        var rcode = data[3] & 15;
        if ((data[2] & 2) != 0)
        {
            response = new DnsResponse(rcode, true, []);
            return true;
        }
        var records = new List<DnsAnswer>();
        var answers = U16(data, 6);
        var total = answers + U16(data, 8) + U16(data, 10);
        for (var i = 0; i < total; i++)
        {
            if (!ReadName(data, ref offset, out var owner) || offset + 10 > data.Length) return false;
            var rrtype = U16(data, offset);
            var rrclass = U16(data, offset + 2);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4, 4));
            var length = U16(data, offset + 8);
            offset += 10;
            var end = offset + length;
            if (end > data.Length) return false;
            string? value = null;
            if (i < answers && rrclass == 1)
            {
                var cursor = offset;
                switch (rrtype)
                {
                    case 1:
                        if (length != 4) return false;
                        value = new IPAddress(data.AsSpan(offset, 4)).ToString(); break;
                    case 28:
                        if (length != 16) return false;
                        value = new IPAddress(data.AsSpan(offset, 16)).ToString(); break;
                    case 2 or 5 or 12:
                        if (!ReadName(data, ref cursor, out value) || cursor != end) return false;
                        break;
                    case 15:
                        if (length < 3) return false;
                        cursor += 2;
                        if (!ReadName(data, ref cursor, out var mx) || cursor != end) return false;
                        value = $"{U16(data, offset)} {mx}"; break;
                    case 16:
                        var text = new List<string>();
                        while (cursor < end)
                        {
                            var size = data[cursor++];
                            if (cursor + size > end) return false;
                            text.Add(Encoding.UTF8.GetString(data, cursor, size)); cursor += size;
                        }
                        value = string.Join(" ", text); break;
                }
                if (value is not null) records.Add(new DnsAnswer(owner, ((DnsRecordType)rrtype).ToString(), ttl, value));
            }
            offset = end;
        }
        // Only records for the queried name or its CNAME chain count as answers.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
        var aliases = records.Where(r => r.Type == "CNAME").ToLookup(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(); pending.Enqueue(name);
        while (pending.TryDequeue(out var owner))
            foreach (var alias in aliases[owner])
                if (names.Add(alias.Value)) pending.Enqueue(alias.Value);
        response = new DnsResponse(rcode, false, records.Where(r => names.Contains(r.Name) && (r.Type == type.ToString() || r.Type == "CNAME")).ToArray());
        return true;
    }

    // Compatibility helper used by packet fixtures.
    public static bool TryParseResponse(byte[] data, ushort id, out int rcode, out string? answer)
    {
        rcode = -1; answer = null;
        var offset = 12;
        if (!ReadName(data, ref offset, out var name) || offset + 4 > data.Length) return false;
        if (!TryParseResponse(data, id, name, (DnsRecordType)U16(data, offset), out var response)) return false;
        rcode = response!.Rcode;
        answer = response.Answers.FirstOrDefault(r => r.Type is "A" or "AAAA")?.Value;
        return true;
    }

    private static bool ReadName(byte[] data, ref int offset, out string name)
    {
        name = "";
        var labels = new List<string>();
        var cursor = offset;
        var jumped = false;
        var totalLength = 0;
        for (var hops = 0; hops < 128 && cursor < data.Length; hops++)
        {
            var position = cursor;
            var length = data[cursor++];
            if (length == 0)
            {
                if (!jumped) offset = cursor;
                name = string.Join('.', labels); return true;
            }
            if ((length & 0xC0) == 0xC0)
            {
                if (cursor >= data.Length) return false;
                var pointer = ((length & 63) << 8) | data[cursor++];
                if (pointer < 12 || pointer >= position) return false;
                if (!jumped) offset = cursor;
                jumped = true; cursor = pointer; continue;
            }
            if (length > 63 || cursor + length > data.Length) return false;
            totalLength += length + 1;
            if (totalLength > 254) return false;
            labels.Add(Encoding.ASCII.GetString(data, cursor, length)); cursor += length;
        }
        return false;
    }

    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    public static string RcodeName(int rcode) => rcode switch
    {
        0 => "NOERROR", 1 => "FORMERR", 2 => "SERVFAIL", 3 => "NXDOMAIN", 4 => "NOTIMP", 5 => "REFUSED", _ => $"RCODE {rcode}"
    };
}

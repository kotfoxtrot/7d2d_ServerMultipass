using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ServerMultipass
{
    internal sealed class MaxMindDb : IDisposable
    {
        private const int MetadataSearchBytes = 128 * 1024;
        private static readonly byte[] MetadataMarker = { 0xAB, 0xCD, 0xEF, (byte)'M', (byte)'a', (byte)'x', (byte)'M', (byte)'i', (byte)'n', (byte)'d', (byte)'.', (byte)'c', (byte)'o', (byte)'m' };

        private readonly FileStream stream;
        private readonly object gate = new object();
        private readonly byte[] node;
        private readonly long nodeCount;
        private readonly int recordSize;
        private readonly int ipVersion;
        private readonly long treeSize;
        private readonly long ipv4Start;

        private MaxMindDb(string path)
        {
            Path = path;
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var metadata = ReadMetadata();
            nodeCount = Convert.ToInt64(metadata["node_count"]);
            recordSize = Convert.ToInt32(metadata["record_size"]);
            ipVersion = Convert.ToInt32(metadata["ip_version"]);
            if (recordSize != 24 && recordSize != 28 && recordSize != 32)
                throw new InvalidDataException("unsupported record size " + recordSize);
            node = new byte[recordSize / 4];
            treeSize = nodeCount * node.Length;
            var start = 0L;
            if (ipVersion == 6)
                for (var i = 0; i < 96 && start < nodeCount; i++)
                    start = Record(start, 0);
            ipv4Start = start;
        }

        public string Path { get; }

        public static MaxMindDb Open(string path)
        {
            return new MaxMindDb(path);
        }

        public string Country(string ip)
        {
            if (!IPAddress.TryParse(ip ?? "", out var address)) return null;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            lock (gate)
            {
                var record = Lookup(address);
                if (record < 0) return null;
                var data = new Decoder(this, treeSize + 16).Decode(record - nodeCount - 16) as Dictionary<string, object>;
                return IsoCode(data, "country") ?? IsoCode(data, "registered_country");
            }
        }

        public void Dispose()
        {
            stream.Dispose();
        }

        private static string IsoCode(Dictionary<string, object> data, string section)
        {
            return data != null && data.TryGetValue(section, out var value) && value is Dictionary<string, object> map
                   && map.TryGetValue("iso_code", out var code)
                ? code as string
                : null;
        }

        private long Lookup(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            long current;
            if (address.AddressFamily == AddressFamily.InterNetwork) current = ipVersion == 6 ? ipv4Start : 0L;
            else if (ipVersion == 6) current = 0L;
            else return -1L;
            for (var i = 0; i < bytes.Length * 8 && current < nodeCount; i++)
                current = Record(current, (bytes[i >> 3] >> (7 - (i & 7))) & 1);
            return current > nodeCount ? current : -1L;
        }

        private long Record(long index, int bit)
        {
            Read(index * node.Length, node, node.Length);
            switch (recordSize)
            {
                case 24:
                    return bit == 0
                        ? (node[0] << 16) | (node[1] << 8) | node[2]
                        : (node[3] << 16) | (node[4] << 8) | node[5];
                case 28:
                    return bit == 0
                        ? ((long)(node[3] & 0xF0) << 20) | ((long)node[0] << 16) | ((long)node[1] << 8) | node[2]
                        : ((long)(node[3] & 0x0F) << 24) | ((long)node[4] << 16) | ((long)node[5] << 8) | node[6];
                default:
                    var offset = bit * 4;
                    return ((long)node[offset] << 24) | ((long)node[offset + 1] << 16) | ((long)node[offset + 2] << 8) | node[offset + 3];
            }
        }

        private void Read(long position, byte[] buffer, int count)
        {
            stream.Position = position;
            var done = 0;
            while (done < count)
            {
                var read = stream.Read(buffer, done, count - done);
                if (read <= 0) throw new EndOfStreamException();
                done += read;
            }
        }

        private Dictionary<string, object> ReadMetadata()
        {
            var length = (int)Math.Min(MetadataSearchBytes, stream.Length);
            var tail = new byte[length];
            var tailStart = stream.Length - length;
            Read(tailStart, tail, length);
            for (var i = length - MetadataMarker.Length; i >= 0; i--)
            {
                var match = true;
                for (var j = 0; j < MetadataMarker.Length && match; j++) match = tail[i + j] == MetadataMarker[j];
                if (!match) continue;
                var metadataStart = tailStart + i + MetadataMarker.Length;
                if (new Decoder(this, metadataStart).Decode(0) is Dictionary<string, object> metadata) return metadata;
                break;
            }
            throw new InvalidDataException("not a MaxMind database");
        }

        private sealed class Decoder
        {
            private readonly MaxMindDb db;
            private readonly long baseOffset;
            private readonly byte[] one = new byte[1];

            public Decoder(MaxMindDb db, long baseOffset)
            {
                this.db = db;
                this.baseOffset = baseOffset;
            }

            public object Decode(long offset)
            {
                return Decode(ref offset, 0);
            }

            private object Decode(ref long offset, int depth)
            {
                if (depth > 32) throw new InvalidDataException("data structure is nested too deeply");
                var control = Byte(offset++);
                var type = control >> 5;
                if (type == 1)
                {
                    var target = Pointer(control, ref offset);
                    return Decode(ref target, depth + 1);
                }
                if (type == 0) type = 7 + Byte(offset++);
                var size = control & 0x1F;
                if (size == 29) size = 29 + Byte(offset++);
                else if (size == 30) size = 285 + (int)Unsigned(ref offset, 2);
                else if (size == 31) size = 65821 + (int)Unsigned(ref offset, 3);
                switch (type)
                {
                    case 2:
                        return Text(ref offset, size);
                    case 3:
                        return BitConverter.Int64BitsToDouble((long)Unsigned(ref offset, 8));
                    case 4:
                        offset += size;
                        return null;
                    case 5:
                    case 6:
                    case 9:
                        return Unsigned(ref offset, size);
                    case 10:
                        offset += size;
                        return null;
                    case 7:
                        var map = new Dictionary<string, object>(size);
                        for (var i = 0; i < size; i++)
                        {
                            var key = Decode(ref offset, depth + 1) as string ?? "";
                            map[key] = Decode(ref offset, depth + 1);
                        }
                        return map;
                    case 8:
                        return (int)Unsigned(ref offset, size);
                    case 11:
                        var list = new List<object>(size);
                        for (var i = 0; i < size; i++) list.Add(Decode(ref offset, depth + 1));
                        return list;
                    case 14:
                        return size != 0;
                    case 15:
                        var bits = (int)Unsigned(ref offset, 4);
                        return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
                    default:
                        throw new InvalidDataException("unknown data type " + type);
                }
            }

            private long Pointer(int control, ref long offset)
            {
                var size = (control >> 3) & 0x3;
                var high = control & 0x7;
                switch (size)
                {
                    case 0:
                        return (high << 8) | Byte(offset++);
                    case 1:
                        return (((long)high << 16) | (long)Unsigned(ref offset, 2)) + 2048;
                    case 2:
                        return (((long)high << 24) | (long)Unsigned(ref offset, 3)) + 526336;
                    default:
                        return (long)Unsigned(ref offset, 4);
                }
            }

            private ulong Unsigned(ref long offset, int count)
            {
                ulong value = 0;
                for (var i = 0; i < count; i++) value = (value << 8) | Byte(offset++);
                return value;
            }

            private string Text(ref long offset, int size)
            {
                var bytes = new byte[size];
                if (size > 0) db.Read(baseOffset + offset, bytes, size);
                offset += size;
                return Encoding.UTF8.GetString(bytes);
            }

            private byte Byte(long offset)
            {
                db.Read(baseOffset + offset, one, 1);
                return one[0];
            }
        }
    }
}

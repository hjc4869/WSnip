using System.Buffers.Binary;
using TextEncoding = System.Text.Encoding;

namespace WSnip.Core.Encoding;

/// <summary>Minimal ISO base media box serialization.</summary>
internal sealed class BoxWriter
{
    private readonly MemoryStream stream = new();

    public long Length => stream.Length;

    public BoxWriter U8(int value)
    {
        stream.WriteByte((byte)value);
        return this;
    }

    public BoxWriter U16(int value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)value);
        stream.Write(buffer);
        return this;
    }

    public BoxWriter U32(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
        return this;
    }

    public BoxWriter FourCc(string code)
    {
        if (code.Length != 4)
            throw new ArgumentException("A four-character code is required.", nameof(code));
        stream.Write(TextEncoding.ASCII.GetBytes(code));
        return this;
    }

    public BoxWriter Bytes(ReadOnlySpan<byte> data)
    {
        stream.Write(data);
        return this;
    }

    public BoxWriter CString(string text)
    {
        stream.Write(TextEncoding.UTF8.GetBytes(text));
        stream.WriteByte(0);
        return this;
    }

    public byte[] ToArray() => stream.ToArray();

    public static byte[] Box(string type, Action<BoxWriter> content)
    {
        var body = new BoxWriter();
        content(body);
        byte[] payload = body.ToArray();
        return new BoxWriter().U32((uint)(payload.Length + 8)).FourCc(type).Bytes(payload).ToArray();
    }

    public static byte[] FullBox(string type, int version, int flags, Action<BoxWriter> content) =>
        Box(type, body =>
        {
            body.U32((uint)((version << 24) | (flags & 0xFFFFFF)));
            content(body);
        });
}

/// <summary>A property box and whether readers must understand it.</summary>
internal readonly record struct HeifProperty(byte[] Box, bool Essential);

/// <summary>An item of a HEIF file: a coded image, a derived image, or metadata.</summary>
internal sealed class HeifItem
{
    public required ushort Id { get; init; }

    public required string Type { get; init; }

    public string Name { get; init; } = string.Empty;

    public bool Hidden { get; init; }

    public required byte[] Data { get; init; }

    public List<HeifProperty> Properties { get; } = [];
}

/// <summary>
/// Writes a still-image HEIF/AVIF file: ftyp, a meta box describing the items, and one mdat
/// holding all item data. Supports a <c>tmap</c> derived image grouped with its base as alternatives.
/// </summary>
internal static class HeifWriter
{
    public static byte[] Ispe(int width, int height) =>
        BoxWriter.FullBox("ispe", 0, 0, b => b.U32((uint)width).U32((uint)height));

    public static byte[] Pixi(int channels, int bits) =>
        BoxWriter.FullBox("pixi", 0, 0, b =>
        {
            b.U8(channels);
            for (int i = 0; i < channels; i++)
                b.U8(bits);
        });

    public static byte[] Nclx(int primaries, int transfer, int matrix, bool fullRange) =>
        BoxWriter.Box("colr", b => b.FourCc("nclx").U16(primaries).U16(transfer).U16(matrix).U8(fullRange ? 0x80 : 0));

    public static byte[] ContentLightLevel(int maxCll, int maxPall) =>
        BoxWriter.Box("clli", b => b.U16(Math.Clamp(maxCll, 0, ushort.MaxValue)).U16(Math.Clamp(maxPall, 0, ushort.MaxValue)));

    public static byte[] Configuration(string type, byte[] record) => BoxWriter.Box(type, b => b.Bytes(record));

    public static void Write(Stream output, string majorBrand, IReadOnlyList<string> compatibleBrands,
        IReadOnlyList<HeifItem> items, ushort primaryItem, IReadOnlyList<(string Type, ushort From, ushort[] To)> references,
        IReadOnlyList<ushort>? alternatives)
    {
        byte[] ftyp = BoxWriter.Box("ftyp", b =>
        {
            b.FourCc(majorBrand).U32(0);
            foreach (string brand in compatibleBrands)
                b.FourCc(brand);
        });

        // Item offsets depend on the size of the meta box, which does not depend on their values.
        byte[] meta = BuildMeta(items, primaryItem, references, alternatives, new uint[items.Count]);
        long mdatHeader = 8;
        long mdatPayload = items.Sum(item => (long)item.Data.Length);
        long dataStart = ftyp.Length + meta.Length + mdatHeader;
        if (dataStart + mdatPayload > uint.MaxValue)
            throw new InvalidDataException("The image is too large for 32-bit item offsets.");

        var offsets = new uint[items.Count];
        long offset = dataStart;
        for (int i = 0; i < items.Count; i++)
        {
            offsets[i] = (uint)offset;
            offset += items[i].Data.Length;
        }

        meta = BuildMeta(items, primaryItem, references, alternatives, offsets);
        output.Write(ftyp);
        output.Write(meta);
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(mdatHeader + mdatPayload));
        TextEncoding.ASCII.GetBytes("mdat", header[4..]);
        output.Write(header);
        foreach (HeifItem item in items)
            output.Write(item.Data);
    }

    private static byte[] BuildMeta(IReadOnlyList<HeifItem> items, ushort primaryItem,
        IReadOnlyList<(string Type, ushort From, ushort[] To)> references, IReadOnlyList<ushort>? alternatives, uint[] offsets)
    {
        var properties = new List<byte[]>();
        var associations = new List<(ushort Item, List<(int Index, bool Essential)> Entries)>();
        foreach (HeifItem item in items)
        {
            var entries = new List<(int, bool)>();
            foreach (HeifProperty property in item.Properties)
            {
                int index = properties.FindIndex(existing => existing.AsSpan().SequenceEqual(property.Box));
                if (index < 0)
                {
                    properties.Add(property.Box);
                    index = properties.Count - 1;
                }

                entries.Add((index + 1, property.Essential));
            }

            if (entries.Count > 0)
                associations.Add((item.Id, entries));
        }

        if (properties.Count > 127)
            throw new InvalidDataException("Too many item properties.");

        return BoxWriter.FullBox("meta", 0, 0, meta =>
        {
            meta.Bytes(BoxWriter.FullBox("hdlr", 0, 0, b => b.U32(0).FourCc("pict").U32(0).U32(0).U32(0).CString(string.Empty)));
            meta.Bytes(BoxWriter.FullBox("pitm", 0, 0, b => b.U16(primaryItem)));
            meta.Bytes(BoxWriter.FullBox("iloc", 0, 0, b =>
            {
                b.U8(0x44).U8(0x00).U16(items.Count);
                for (int i = 0; i < items.Count; i++)
                    b.U16(items[i].Id).U16(0).U16(1).U32(offsets[i]).U32((uint)items[i].Data.Length);
            }));
            meta.Bytes(BoxWriter.FullBox("iinf", 0, 0, b =>
            {
                b.U16(items.Count);
                foreach (HeifItem item in items)
                {
                    b.Bytes(BoxWriter.FullBox("infe", 2, item.Hidden ? 1 : 0,
                        entry => entry.U16(item.Id).U16(0).FourCc(item.Type).CString(item.Name)));
                }
            }));
            if (references.Count > 0)
            {
                meta.Bytes(BoxWriter.FullBox("iref", 0, 0, b =>
                {
                    foreach ((string type, ushort from, ushort[] to) in references)
                    {
                        b.Bytes(BoxWriter.Box(type, reference =>
                        {
                            reference.U16(from).U16(to.Length);
                            foreach (ushort target in to)
                                reference.U16(target);
                        }));
                    }
                }));
            }

            meta.Bytes(BoxWriter.Box("iprp", iprp =>
            {
                iprp.Bytes(BoxWriter.Box("ipco", ipco =>
                {
                    foreach (byte[] property in properties)
                        ipco.Bytes(property);
                }));
                iprp.Bytes(BoxWriter.FullBox("ipma", 0, 0, ipma =>
                {
                    ipma.U32((uint)associations.Count);
                    foreach ((ushort item, List<(int Index, bool Essential)> entries) in associations)
                    {
                        ipma.U16(item).U8(entries.Count);
                        foreach ((int index, bool essential) in entries)
                            ipma.U8((essential ? 0x80 : 0) | index);
                    }
                }));
            }));

            if (alternatives is { Count: > 1 })
            {
                uint groupId = (uint)items.Max(item => item.Id) + 1;
                meta.Bytes(BoxWriter.Box("grpl", grpl => grpl.Bytes(BoxWriter.FullBox("altr", 0, 0, altr =>
                {
                    altr.U32(groupId).U32((uint)alternatives.Count);
                    foreach (ushort id in alternatives)
                        altr.U32(id);
                }))));
            }
        });
    }
}

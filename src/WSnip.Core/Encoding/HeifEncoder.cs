using FFmpeg.AutoGen.Abstractions;
using WSnip.Core.Imaging;

using WSnip.Core.Strings;

namespace WSnip.Core.Encoding;

public enum HeifCodec
{
    /// <summary>HEIC: HEVC Main profile, 4:2:0.</summary>
    Hevc,

    /// <summary>AVIF: AV1, 4:4:4 when the encoder supports it.</summary>
    Av1,
}

/// <summary>
/// Writes HEIC and AVIF files whose primary item is the SDR base image. When the rendition has a
/// gain map, an ISO 21496-1 <c>tmap</c> derived image item combines the base and a hidden gain map
/// item, and is grouped with the base in an <c>altr</c> group so HDR-aware readers prefer it.
/// </summary>
public static class HeifEncoder
{
    private const ushort BaseId = 1;
    private const ushort ToneMapId = 2;
    private const ushort GainMapId = 3;

    private static readonly string[] HevcEncoders = ["libkvazaar", "libx265"];
    private static readonly string[] Av1Encoders = ["libaom-av1", "librav1e", "libsvtav1"];

    public static bool IsAvailable(HeifCodec codec) => FindEncoder(codec) is not null;

    /// <summary>Encodes a rendition built with flattened alpha.</summary>
    /// <param name="quality">1 to 100, where 100 is near-lossless.</param>
    public static void Write(Stream output, Rendition rendition, HeifCodec codec, int quality, int gainMapQuality)
    {
        string encoder = FindEncoder(codec)
            ?? throw new NotSupportedException(string.Format(AppStrings.EncoderUnavailable, codec == HeifCodec.Hevc ? "HEVC" : "AV1"));
        var coder = new ItemCoder(codec, encoder);
        int primariesCode = ColorMath.CicpPrimaries(rendition.Primaries);

        var items = new List<HeifItem>();
        HeifItem baseItem = coder.EncodeColor(BaseId, rendition, quality, primariesCode);
        items.Add(baseItem);
        var references = new List<(string, ushort, ushort[])>();
        ushort[]? alternatives = null;

        if (rendition.GainMap is { } gainMap)
        {
            HeifItem gainItem = coder.EncodeGainMap(GainMapId, gainMap, gainMapQuality);
            byte[] metadata = [0, .. gainMap.Metadata.ToIsoBinary()];
            var toneMap = new HeifItem { Id = ToneMapId, Type = "tmap", Name = "TMap", Data = metadata };
            toneMap.Properties.Add(new HeifProperty(HeifWriter.Ispe(rendition.Width, rendition.Height), false));
            toneMap.Properties.Add(new HeifProperty(HeifWriter.Pixi(3, 10), false));
            toneMap.Properties.Add(new HeifProperty(HeifWriter.Nclx(primariesCode, ColorMath.CicpTransferPq, ColorMath.CicpMatrixIdentity, true), false));
            int maxCll = (int)Math.Ceiling(rendition.Peak * ColorMath.ReferenceWhiteNits);
            toneMap.Properties.Add(new HeifProperty(HeifWriter.ContentLightLevel(maxCll, 0), false));
            items.Add(toneMap);
            items.Add(gainItem);
            references.Add(("dimg", ToneMapId, [BaseId, GainMapId]));
            alternatives = [ToneMapId, BaseId];
        }

        var brands = new List<string>();
        if (codec == HeifCodec.Hevc)
        {
            brands.AddRange(["mif1", "heic", "miaf"]);
        }
        else
        {
            brands.AddRange(["avif", "mif1", "miaf"]);
            string? profile = coder.Av1ProfileBrand();
            if (profile is not null)
                brands.Add(profile);
        }

        if (rendition.GainMap is not null)
            brands.Add("tmap");
        HeifWriter.Write(output, codec == HeifCodec.Hevc ? "heic" : "avif", brands, items, BaseId, references, alternatives);
    }

    private static string? FindEncoder(HeifCodec codec) =>
        FfmpegStillEncoder.FirstAvailable(codec == HeifCodec.Hevc ? HevcEncoders : Av1Encoders);

    private sealed class ItemCoder(HeifCodec codec, string encoder)
    {
        private readonly List<Av1Bitstream.SequenceInfo> av1Sequences = [];

        private bool FullChroma => codec == HeifCodec.Av1 && encoder == "libaom-av1";

        public HeifItem EncodeColor(ushort id, Rendition rendition, int quality, int primariesCode)
        {
            (int codedWidth, int codedHeight) = CodedSize(rendition.Width, rendition.Height);
            byte[] rgba = Pad(rendition.Sdr, rendition.Width, rendition.Height, 4, codedWidth, codedHeight);
            bool subsample = !FullChroma;
            (byte[][] planes, int[] strides) = YuvConverter.FromRgba(rgba, codedWidth, codedHeight, subsample, YuvMatrix.Bt709);
            var picture = new PicturePlanes
            {
                Width = codedWidth,
                Height = codedHeight,
                Format = subsample ? AVPixelFormat.AV_PIX_FMT_YUV420P : AVPixelFormat.AV_PIX_FMT_YUV444P,
                Planes = planes,
                Strides = strides,
                Primaries = (AVColorPrimaries)primariesCode,
                Transfer = AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1,
                Matrix = AVColorSpace.AVCOL_SPC_BT709,
                Range = AVColorRange.AVCOL_RANGE_JPEG,
            };

            HeifItem item = Encode(id, picture, quality, "Color", hidden: false);
            item.Properties.Add(new HeifProperty(HeifWriter.Pixi(3, 8), false));
            item.Properties.Add(new HeifProperty(HeifWriter.Nclx(primariesCode, ColorMath.CicpTransferSrgb, ColorMath.CicpMatrixBt709, true), false));
            AddCrop(item, rendition.Width, rendition.Height, codedWidth, codedHeight);
            return item;
        }

        public HeifItem EncodeGainMap(ushort id, GainMapImage gainMap, int quality)
        {
            (int codedWidth, int codedHeight) = CodedSize(gainMap.Width, gainMap.Height);
            byte[] samples = Pad(gainMap.Pixels, gainMap.Width, gainMap.Height, gainMap.Channels, codedWidth, codedHeight);
            PicturePlanes picture;
            int matrix;
            if (gainMap.Channels == 3 && FullChroma)
            {
                (byte[][] planes, int[] strides) = YuvConverter.GbrFromRgb(samples, codedWidth, codedHeight);
                picture = new PicturePlanes
                {
                    Width = codedWidth,
                    Height = codedHeight,
                    Format = AVPixelFormat.AV_PIX_FMT_GBRP,
                    Planes = planes,
                    Strides = strides,
                    Matrix = AVColorSpace.AVCOL_SPC_RGB,
                };
                matrix = ColorMath.CicpMatrixIdentity;
            }
            else if (gainMap.Channels == 1 && FullChroma)
            {
                picture = new PicturePlanes
                {
                    Width = codedWidth,
                    Height = codedHeight,
                    Format = AVPixelFormat.AV_PIX_FMT_GRAY8,
                    Planes = [samples],
                    Strides = [codedWidth],
                    Matrix = AVColorSpace.AVCOL_SPC_BT470BG,
                };
                matrix = ColorMath.CicpMatrixBt601;
            }
            else
            {
                byte[] rgba = YuvConverter.ToRgba(samples, codedWidth, codedHeight, gainMap.Channels);
                (byte[][] planes, int[] strides) = YuvConverter.FromRgba(rgba, codedWidth, codedHeight, true, YuvMatrix.Bt601);
                picture = new PicturePlanes
                {
                    Width = codedWidth,
                    Height = codedHeight,
                    Format = AVPixelFormat.AV_PIX_FMT_YUV420P,
                    Planes = planes,
                    Strides = strides,
                    Matrix = AVColorSpace.AVCOL_SPC_BT470BG,
                };
                matrix = ColorMath.CicpMatrixBt601;
            }

            HeifItem item = Encode(id, picture, quality, "GMap", hidden: true);
            int channels = picture.Format == AVPixelFormat.AV_PIX_FMT_GRAY8 ? 1 : 3;
            item.Properties.Add(new HeifProperty(HeifWriter.Pixi(channels, 8), false));
            item.Properties.Add(new HeifProperty(HeifWriter.Nclx(2, 2, matrix, true), false));
            AddCrop(item, gainMap.Width, gainMap.Height, codedWidth, codedHeight);
            return item;
        }

        /// <summary>The AVIF profile brand every AV1 item of the file satisfies, if any.</summary>
        public string? Av1ProfileBrand()
        {
            if (av1Sequences.Count == 0)
                return null;
            if (av1Sequences.All(s => s.Profile == 0 && s.Level <= 13))
                return "MA1B";
            if (av1Sequences.All(s => s.Profile <= 1 && s.Level <= 16))
                return "MA1A";
            return null;
        }

        private HeifItem Encode(ushort id, PicturePlanes picture, int quality, string name, bool hidden)
        {
            byte[] packet = FfmpegStillEncoder.Encode(encoder, picture, EncoderOptions(quality));
            byte[] data;
            byte[] configuration;
            if (codec == HeifCodec.Hevc)
            {
                List<byte[]> units = HevcBitstream.SplitAnnexB(packet);
                List<byte[]> vps = units.Where(u => HevcBitstream.NalType(u) == HevcBitstream.VpsType).ToList();
                List<byte[]> sps = units.Where(u => HevcBitstream.NalType(u) == HevcBitstream.SpsType).ToList();
                List<byte[]> pps = units.Where(u => HevcBitstream.NalType(u) == HevcBitstream.PpsType).ToList();
                if (sps.Count == 0)
                    throw new InvalidDataException("The HEVC encoder produced no sequence parameter set.");
                HevcBitstream.SequenceInfo info = HevcBitstream.ParseSps(sps[0]);
                if (info.Width != picture.Width || info.Height != picture.Height)
                    throw new InvalidDataException($"The HEVC stream is {info.Width}x{info.Height}, expected {picture.Width}x{picture.Height}.");
                configuration = HeifWriter.Configuration("hvcC", HevcBitstream.ConfigurationRecord(info, vps, sps, pps));
                data = HevcBitstream.LengthPrefixed(units.Where(u => HevcBitstream.NalType(u) is not
                    (HevcBitstream.VpsType or HevcBitstream.SpsType or HevcBitstream.PpsType or HevcBitstream.AudType)));
            }
            else
            {
                (data, byte[] sequenceHeader) = Av1Bitstream.Prepare(packet);
                Av1Bitstream.SequenceInfo info = Av1Bitstream.ParseSequenceHeader(sequenceHeader);
                if (info.Width != picture.Width || info.Height != picture.Height)
                    throw new InvalidDataException($"The AV1 stream is {info.Width}x{info.Height}, expected {picture.Width}x{picture.Height}.");
                av1Sequences.Add(info);
                configuration = HeifWriter.Configuration("av1C", Av1Bitstream.ConfigurationRecord(info));
            }

            var item = new HeifItem
            {
                Id = id,
                Type = codec == HeifCodec.Hevc ? "hvc1" : "av01",
                Name = name,
                Hidden = hidden,
                Data = data,
            };
            item.Properties.Add(new HeifProperty(configuration, true));
            item.Properties.Add(new HeifProperty(HeifWriter.Ispe(picture.Width, picture.Height), false));
            return item;
        }

        private Dictionary<string, string> EncoderOptions(int quality)
        {
            int q = Math.Clamp(quality, 1, 100);
            return encoder switch
            {
                "libkvazaar" => new() { ["kvazaar-params"] = $"qp={HevcQp(q)},preset=medium" },
                "libx265" => new() { ["x265-params"] = $"qp={HevcQp(q)}:keyint=1:info=0", ["preset"] = "medium" },
                "libaom-av1" => new()
                {
                    ["crf"] = Av1Quantizer(q).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["usage"] = "allintra",
                    ["cpu-used"] = "6",
                    ["row-mt"] = "1",
                    ["still-picture"] = "1",
                },
                "librav1e" => new()
                {
                    ["qp"] = (Av1Quantizer(q) * 4).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["speed"] = "8",
                },
                _ => new()
                {
                    ["crf"] = Av1Quantizer(q).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["preset"] = "8",
                },
            };
        }

        // Kvazaar only accepts multiples of 8; the padding is cropped away again by a 'clap' property.
        private (int Width, int Height) CodedSize(int width, int height) =>
            codec == HeifCodec.Hevc
                ? (Math.Max(16, (width + 7) & ~7), Math.Max(16, (height + 7) & ~7))
                : (width, height);

        private static void AddCrop(HeifItem item, int width, int height, int codedWidth, int codedHeight)
        {
            if (width == codedWidth && height == codedHeight)
                return;
            byte[] clap = BoxWriter.Box("clap", b => b
                .U32((uint)width).U32(1)
                .U32((uint)height).U32(1)
                .U32(unchecked((uint)(width - codedWidth))).U32(2)
                .U32(unchecked((uint)(height - codedHeight))).U32(2));
            item.Properties.Add(new HeifProperty(clap, true));
        }

        private static int HevcQp(int quality) => (int)Math.Round(4 + (100 - quality) * 0.42);

        private static int Av1Quantizer(int quality) => ((100 - quality) * 63 + 50) / 100;
    }

    private static byte[] Pad(byte[] samples, int width, int height, int channels, int codedWidth, int codedHeight)
    {
        if (width == codedWidth && height == codedHeight)
            return samples;
        var padded = new byte[codedWidth * codedHeight * channels];
        for (int y = 0; y < codedHeight; y++)
        {
            int sourceRow = Math.Min(y, height - 1) * width * channels;
            int targetRow = y * codedWidth * channels;
            Buffer.BlockCopy(samples, sourceRow, padded, targetRow, width * channels);
            for (int x = width; x < codedWidth; x++)
                Buffer.BlockCopy(samples, sourceRow + (width - 1) * channels, padded, targetRow + x * channels, channels);
        }

        return padded;
    }
}

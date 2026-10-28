using LightStudio.FfmpegShim.ImageEncoding;
using LightStudio.NativeBindings.Types;
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
    public static bool IsAvailable(HeifCodec codec) => FindEncoder(codec) is not null;

    /// <summary>Encodes a rendition built with flattened alpha.</summary>
    /// <param name="quality">1 to 100, where 100 is near-lossless.</param>
    public static void Write(Stream output, Rendition rendition, HeifCodec codec, int quality, int gainMapQuality)
    {
        StillImageEncoderInfo encoder = FindEncoder(codec)
            ?? throw new NotSupportedException(string.Format(AppStrings.EncoderUnavailable, codec == HeifCodec.Hevc ? "HEVC" : "AV1"));
        var coder = new ItemCoder(codec, encoder);
        int primariesCode = ColorMath.CicpPrimaries(rendition.Primaries);

        ushort baseId = coder.EncodeColor(rendition, quality, primariesCode);
        ushort[]? alternatives = null;

        if (rendition.GainMap is { } gainMap)
        {
            ushort gainId = coder.EncodeGainMap(gainMap, gainMapQuality);
            ushort toneMapId = coder.NextId();
            byte[] metadata = [0, .. gainMap.Metadata.ToIsoBinary()];
            var toneMap = new HeifItem { Id = toneMapId, Type = "tmap", Name = "TMap", Data = metadata };
            toneMap.Properties.Add(new HeifProperty(HeifWriter.Ispe(rendition.Width, rendition.Height), false));
            toneMap.Properties.Add(new HeifProperty(HeifWriter.Pixi(3, 10), false));
            toneMap.Properties.Add(new HeifProperty(HeifWriter.Nclx(primariesCode, ColorMath.CicpTransferPq, ColorMath.CicpMatrixIdentity, true), false));
            int maxCll = (int)Math.Ceiling(rendition.Peak * ColorMath.ReferenceWhiteNits);
            toneMap.Properties.Add(new HeifProperty(HeifWriter.ContentLightLevel(maxCll, 0), false));
            coder.Items.Add(toneMap);
            coder.References.Add(("dimg", toneMapId, [baseId, gainId]));
            alternatives = [toneMapId, baseId];
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
        HeifWriter.Write(output, codec == HeifCodec.Hevc ? "heic" : "avif", brands, coder.Items, baseId, coder.References, alternatives);
    }

    private static StillImageEncoderInfo? FindEncoder(HeifCodec codec) =>
        StillImageEncoders.ProbeAsync().GetAwaiter().GetResult().FirstOrDefault(encoder =>
            encoder.Codec == (codec == HeifCodec.Hevc ? StillImageCodec.Hevc : StillImageCodec.Av1) &&
            (encoder.Supports(AVPixelFormat.AV_PIX_FMT_YUV420P) ||
                (codec == HeifCodec.Av1 && encoder.Supports(AVPixelFormat.AV_PIX_FMT_YUV444P)) ||
                encoder.Supports(AVPixelFormat.AV_PIX_FMT_NV12) || encoder.Supports(AVPixelFormat.AV_PIX_FMT_P010LE)));

    private sealed class ItemCoder(HeifCodec codec, StillImageEncoderInfo encoder)
    {
        private readonly List<Av1Bitstream.SequenceInfo> av1Sequences = [];
        private ushort lastId;

        public List<HeifItem> Items { get; } = [];

        public List<(string, ushort, ushort[])> References { get; } = [];

        public ushort NextId() => checked(++lastId);

        private bool FullChroma => codec == HeifCodec.Av1 && encoder.Supports(AVPixelFormat.AV_PIX_FMT_YUV444P);

        public ushort EncodeColor(Rendition rendition, int quality, int primariesCode)
        {
            bool subsample = !FullChroma;
            (PixelBuffer<byte>[] planes, int[] strides) = YuvConverter.FromRgba(rendition.Sdr, rendition.Width, rendition.Height, subsample, YuvMatrix.Bt709);
            using var picture = new PicturePlanes
            {
                Width = rendition.Width,
                Height = rendition.Height,
                Format = subsample ? AVPixelFormat.AV_PIX_FMT_YUV420P : AVPixelFormat.AV_PIX_FMT_YUV444P,
                Planes = planes,
                Strides = strides,
                Primaries = (AVColorPrimaries)primariesCode,
                Transfer = AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1,
                Matrix = AVColorSpace.AVCOL_SPC_BT709,
                Range = AVColorRange.AVCOL_RANGE_JPEG,
            };
            return AddImage(picture, quality, "Color", hidden: false, 3);
        }

        public ushort EncodeGainMap(GainMapImage gainMap, int quality)
        {
            int width = gainMap.Width, height = gainMap.Height;
            PixelBuffer<byte> samples = gainMap.Pixels;
            PicturePlanes picture;
            if (gainMap.Channels == 3 && FullChroma && encoder.Supports(AVPixelFormat.AV_PIX_FMT_GBRP))
            {
                (PixelBuffer<byte>[] planes, int[] strides) = YuvConverter.GbrFromRgb(samples, width, height);
                picture = new PicturePlanes
                {
                    Width = width,
                    Height = height,
                    Format = AVPixelFormat.AV_PIX_FMT_GBRP,
                    Planes = planes,
                    Strides = strides,
                    Matrix = AVColorSpace.AVCOL_SPC_RGB,
                };
            }
            else if (gainMap.Channels == 1 && FullChroma && encoder.Supports(AVPixelFormat.AV_PIX_FMT_GRAY8))
            {
                picture = new PicturePlanes
                {
                    Width = width,
                    Height = height,
                    Format = AVPixelFormat.AV_PIX_FMT_GRAY8,
                    Planes = [samples.Share()],
                    Strides = [width],
                    Matrix = AVColorSpace.AVCOL_SPC_BT470BG,
                };
            }
            else
            {
                using PixelBuffer<byte> rgba = YuvConverter.ToRgba(samples, width, height, gainMap.Channels);
                (PixelBuffer<byte>[] planes, int[] strides) = YuvConverter.FromRgba(rgba, width, height, !FullChroma, YuvMatrix.Bt601);
                picture = new PicturePlanes
                {
                    Width = width,
                    Height = height,
                    Format = FullChroma ? AVPixelFormat.AV_PIX_FMT_YUV444P : AVPixelFormat.AV_PIX_FMT_YUV420P,
                    Planes = planes,
                    Strides = strides,
                    Matrix = AVColorSpace.AVCOL_SPC_BT470BG,
                };
            }

            using PicturePlanes ownedPicture = picture;
            int channels = picture.Format == AVPixelFormat.AV_PIX_FMT_GRAY8 ? 1 : 3;
            return AddImage(picture, quality, "GMap", hidden: true, channels);
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

        private ushort AddImage(PicturePlanes planes, int quality, string name, bool hidden, int channels)
        {
            using StillPicture picture = CreatePicture(planes, encoder);
            planes.Dispose();
            int bitDepth = picture.Format == AVPixelFormat.AV_PIX_FMT_P010LE ? 10 : 8;
            HeifProperty[] description =
            [
                new(HeifWriter.Pixi(channels, bitDepth), false),
                new(HeifWriter.Nclx((int)picture.Primaries, (int)picture.Transfer, (int)picture.Matrix, true), false),
            ];
            int maximum = encoder.MaximumDimension;
            if (picture.Width <= maximum && picture.Height <= maximum)
            {
                (int width, int height) = CodedSize(picture.Width, picture.Height);
                using StillPicture? padded = width == picture.Width && height == picture.Height
                    ? null : picture.Crop(0, 0, width, height);
                HeifItem item = Encode(padded ?? picture, quality, name, hidden, (picture.Width, picture.Height));
                item.Properties.AddRange(description);
                Items.Add(item);
                return item.Id;
            }

            int tileSize = maximum / 2;
            int columns = (picture.Width + tileSize - 1) / tileSize;
            int rows = (picture.Height + tileSize - 1) / tileSize;
            if (columns > 256 || rows > 256)
                throw new InvalidDataException("The image is too large for a HEIF grid.");
            var tiles = new ushort[checked(rows * columns)];
            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    using StillPicture tile = picture.Crop(column * tileSize, row * tileSize, tileSize, tileSize);
                    HeifItem item = Encode(tile, quality, "Tile", hidden: true);
                    item.Properties.AddRange(description);
                    Items.Add(item);
                    tiles[row * columns + column] = item.Id;
                }
            }

            var grid = new HeifItem
            {
                Id = NextId(),
                Type = "grid",
                Name = name,
                Hidden = hidden,
                Data = new BoxWriter().U8(0).U8(1).U8((byte)(rows - 1)).U8((byte)(columns - 1))
                    .U32((uint)picture.Width).U32((uint)picture.Height).ToArray(),
            };
            grid.Properties.Add(new HeifProperty(HeifWriter.Ispe(picture.Width, picture.Height), false));
            grid.Properties.AddRange(description);
            Items.Add(grid);
            References.Add(("dimg", grid.Id, tiles));
            return grid.Id;
        }

        private HeifItem Encode(StillPicture picture, int quality, string name, bool hidden, (int Width, int Height)? visibleSize = null)
        {
            byte[] packet = StillImageEncoder.Encode(encoder, picture, new StillEncodeSettings(quality, StillEncodeSpeed.Balanced));
            byte[] data;
            byte[] configuration;
            int width, height;
            if (codec == HeifCodec.Hevc)
            {
                List<byte[]> units = HevcBitstream.SplitAnnexB(packet);
                List<byte[]> vps = units.Where(u => HevcBitstream.NalType(u) == HevcBitstream.VpsType).ToList();
                List<byte[]> sps = units.Where(u => HevcBitstream.NalType(u) == HevcBitstream.SpsType).ToList();
                List<byte[]> pps = units.Where(u => HevcBitstream.NalType(u) == HevcBitstream.PpsType).ToList();
                if (sps.Count == 0)
                    throw new InvalidDataException("The HEVC encoder produced no sequence parameter set.");
                HevcBitstream.SequenceInfo info = HevcBitstream.ParseSps(sps[0]);
                (width, height) = (info.Width, info.Height);
                configuration = HeifWriter.Configuration("hvcC", HevcBitstream.ConfigurationRecord(info, vps, sps, pps));
                data = HevcBitstream.LengthPrefixed(units.Where(u => HevcBitstream.NalType(u) is not
                    (HevcBitstream.VpsType or HevcBitstream.SpsType or HevcBitstream.PpsType or HevcBitstream.AudType)));
            }
            else
            {
                (data, byte[] sequenceHeader) = Av1Bitstream.Prepare(packet);
                Av1Bitstream.SequenceInfo info = Av1Bitstream.ParseSequenceHeader(sequenceHeader);
                (width, height) = (info.Width, info.Height);
                av1Sequences.Add(info);
                configuration = HeifWriter.Configuration("av1C", Av1Bitstream.ConfigurationRecord(info));
            }

            if (width < picture.Width || height < picture.Height)
                throw new InvalidDataException($"The {encoder.Name} stream is {width}x{height}, expected {picture.Width}x{picture.Height}.");
            var item = new HeifItem
            {
                Id = NextId(),
                Type = codec == HeifCodec.Hevc ? "hvc1" : "av01",
                Name = name,
                Hidden = hidden,
                Data = data,
            };
            item.Properties.Add(new HeifProperty(configuration, true));
            item.Properties.Add(new HeifProperty(HeifWriter.Ispe(width, height), false));
            AddCrop(item, visibleSize?.Width ?? picture.Width, visibleSize?.Height ?? picture.Height, width, height);
            return item;
        }

        private (int Width, int Height) CodedSize(int width, int height)
        {
            (int alignment, int minimum) = encoder.IsHardware ? (64, 256)
                : codec == HeifCodec.Hevc || encoder.Name == "libsvtav1" ? (8, 64) : (2, 16);
            return (Math.Max(minimum, (width + alignment - 1) / alignment * alignment),
                Math.Max(minimum, (height + alignment - 1) / alignment * alignment));
        }

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

    }

    private static unsafe StillPicture CreatePicture(PicturePlanes source, StillImageEncoderInfo encoder)
    {
        AVPixelFormat format = encoder.Supports(source.Format) ? source.Format
            : encoder.Supports(AVPixelFormat.AV_PIX_FMT_NV12) ? AVPixelFormat.AV_PIX_FMT_NV12 : AVPixelFormat.AV_PIX_FMT_P010LE;
        var picture = new StillPicture(source.Width, source.Height, format)
        {
            Primaries = source.Primaries,
            Transfer = source.Transfer,
            Matrix = source.Matrix,
            Range = source.Range,
        };
        try
        {
            for (int plane = 0; plane < picture.PlaneCount; plane++)
            {
                int width = picture.GetPlaneWidth(plane), height = picture.GetPlaneHeight(plane);
                byte* destination = (byte*)picture.GetPlane(plane);
                int stride = picture.GetStride(plane);
                for (int row = 0; row < height; row++)
                {
                    Span<byte> target = new(destination + (long)row * stride, stride);
                    ReadOnlySpan<byte> samples = source.Planes[plane].AsSpan(row * source.Strides[plane], source.Strides[plane]);
                    if (format == source.Format)
                    {
                        samples.CopyTo(target);
                        continue;
                    }

                    bool wide = format == AVPixelFormat.AV_PIX_FMT_P010LE;
                    ReadOnlySpan<byte> cr = plane == 0 ? [] : source.Planes[2].AsSpan(row * source.Strides[2], source.Strides[2]);
                    for (int column = 0; column < width; column++)
                    {
                        int index = plane == 0 ? column : column * 2;
                        if (wide)
                        {
                            Span<ushort> words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(target);
                            words[index] = (ushort)(samples[column] << 8);
                            if (plane != 0)
                                words[index + 1] = (ushort)(cr[column] << 8);
                        }
                        else
                        {
                            target[index] = samples[column];
                            if (plane != 0)
                                target[index + 1] = cr[column];
                        }
                    }
                }
            }

            return picture;
        }
        catch
        {
            picture.Dispose();
            throw;
        }
    }
}

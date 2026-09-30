using FFmpeg.AutoGen.Abstractions;
using LightStudio.FfmpegShim.Interop;

namespace WSnip.Core.Encoding;

/// <summary>Planes of one picture handed to an FFmpeg encoder.</summary>
internal sealed class PicturePlanes
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required AVPixelFormat Format { get; init; }

    /// <summary>Packed rows of each plane, in FFmpeg plane order.</summary>
    public required byte[][] Planes { get; init; }

    public required int[] Strides { get; init; }

    public AVColorPrimaries Primaries { get; init; } = AVColorPrimaries.AVCOL_PRI_UNSPECIFIED;

    public AVColorTransferCharacteristic Transfer { get; init; } = AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED;

    public AVColorSpace Matrix { get; init; } = AVColorSpace.AVCOL_SPC_UNSPECIFIED;

    public AVColorRange Range { get; init; } = AVColorRange.AVCOL_RANGE_JPEG;

    public AVChromaLocation ChromaLocation { get; init; } = AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED;
}

/// <summary>Encodes single pictures with FFmpeg's video and image encoders.</summary>
internal static unsafe class FfmpegStillEncoder
{
    public static bool IsAvailable(string encoderName)
    {
        try
        {
            return ffmpeg.avcodec_find_encoder_by_name(encoderName) != null;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Returns the first encoder of a list that the loaded FFmpeg provides.</summary>
    public static string? FirstAvailable(IEnumerable<string> encoderNames) => encoderNames.FirstOrDefault(IsAvailable);

    /// <summary>Encodes the picture and returns the concatenated packet payloads.</summary>
    public static byte[] Encode(string encoderName, PicturePlanes picture, IReadOnlyDictionary<string, string> options)
    {
        AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(encoderName);
        if (codec == null)
            throw new NotSupportedException($"The FFmpeg encoder '{encoderName}' is not available.");

        AVCodecContext* context = ffmpeg.avcodec_alloc_context3(codec);
        AVFrame* frame = null;
        AVPacket* packet = null;
        AVDictionary* dictionary = null;
        try
        {
            context->width = picture.Width;
            context->height = picture.Height;
            context->pix_fmt = picture.Format;
            context->time_base = new AVRational { num = 1, den = 25 };
            context->framerate = new AVRational { num = 25, den = 1 };
            context->gop_size = 1;
            context->max_b_frames = 0;
            context->thread_count = 0;
            context->color_primaries = picture.Primaries;
            context->color_trc = picture.Transfer;
            context->colorspace = picture.Matrix;
            context->color_range = picture.Range;
            context->chroma_sample_location = picture.ChromaLocation;

            foreach ((string key, string value) in options)
                ffmpeg.av_dict_set(&dictionary, key, value, 0);
            FfmpegError.ThrowIfError(ffmpeg.avcodec_open2(context, codec, &dictionary), $"Opening {encoderName}");

            frame = ffmpeg.av_frame_alloc();
            frame->format = (int)picture.Format;
            frame->width = picture.Width;
            frame->height = picture.Height;
            frame->color_primaries = picture.Primaries;
            frame->color_trc = picture.Transfer;
            frame->colorspace = picture.Matrix;
            frame->color_range = picture.Range;
            frame->chroma_location = picture.ChromaLocation;
            frame->pts = 0;
            FfmpegError.ThrowIfError(ffmpeg.av_frame_get_buffer(frame, 64), "av_frame_get_buffer");
            FfmpegError.ThrowIfError(ffmpeg.av_frame_make_writable(frame), "av_frame_make_writable");
            for (uint plane = 0; plane < (uint)picture.Planes.Length; plane++)
            {
                byte[] source = picture.Planes[plane];
                int stride = picture.Strides[plane];
                int rows = source.Length / stride;
                byte* target = frame->data[plane];
                int targetStride = frame->linesize[plane];
                fixed (byte* data = source)
                {
                    for (int row = 0; row < rows; row++)
                        Buffer.MemoryCopy(data + (long)row * stride, target + (long)row * targetStride, targetStride, stride);
                }
            }

            packet = ffmpeg.av_packet_alloc();
            using var output = new MemoryStream();
            FfmpegError.ThrowIfError(ffmpeg.avcodec_send_frame(context, frame), $"Encoding with {encoderName}");
            FfmpegError.ThrowIfError(ffmpeg.avcodec_send_frame(context, null), $"Flushing {encoderName}");
            while (true)
            {
                int received = ffmpeg.avcodec_receive_packet(context, packet);
                if (FfmpegError.IsEndOfFile(received) || FfmpegError.IsTryAgain(received))
                    break;
                FfmpegError.ThrowIfError(received, $"Receiving from {encoderName}");
                output.Write(new ReadOnlySpan<byte>(packet->data, packet->size));
                ffmpeg.av_packet_unref(packet);
            }

            if (output.Length == 0)
                throw new InvalidDataException($"{encoderName} produced no data.");
            return output.ToArray();
        }
        finally
        {
            ffmpeg.av_dict_free(&dictionary);
            ffmpeg.av_packet_free(&packet);
            ffmpeg.av_frame_free(&frame);
            ffmpeg.avcodec_free_context(&context);
        }
    }
}

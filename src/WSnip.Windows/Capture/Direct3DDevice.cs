using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using WSnip.Core.Imaging;
using WSnip.Windows.Interop;

namespace WSnip.Windows.Capture;

/// <summary>A Direct3D 11 device shared with Windows.Graphics.Capture, and FP16 texture readback.</summary>
internal sealed unsafe class Direct3DDevice : IDisposable
{
    private const int HardwareDriver = 1;
    private const uint BgraSupport = 0x20;
    private const uint SdkVersion = 7;
    private const uint FormatRgba16Float = 10;
    private const uint StagingUsage = 3;
    private const uint CpuRead = 0x20000;
    private const int MapRead = 1;

    private static readonly Guid DxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid DxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private readonly object gate = new();
    private nint device;
    private nint context;

    private Direct3DDevice(nint device, nint context, IDirect3DDevice winRTDevice)
    {
        this.device = device;
        this.context = context;
        WinRTDevice = winRTDevice;
    }

    public IDirect3DDevice WinRTDevice { get; }

    public static Direct3DDevice Create()
    {
        int* levels = stackalloc int[] { 0xb100, 0xb000 };
        nint device, context;
        int level;
        Native.ThrowIfFailed(Native.D3D11CreateDevice(0, HardwareDriver, 0, BgraSupport, levels, 2, SdkVersion,
            &device, &level, &context), "D3D11CreateDevice");
        nint dxgi = 0, inspectable = 0;
        try
        {
            dxgi = Vtbl.QueryInterface(device, DxgiDevice);
            Native.ThrowIfFailed(Native.CreateDirect3D11DeviceFromDXGIDevice(dxgi, &inspectable), "CreateDirect3D11DeviceFromDXGIDevice");
            IDirect3DDevice winRT = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            return new Direct3DDevice(device, context, winRT);
        }
        catch
        {
            Vtbl.Release(context);
            Vtbl.Release(device);
            throw;
        }
        finally
        {
            Vtbl.Release(inspectable);
            Vtbl.Release(dxgi);
        }
    }

    /// <summary>Copies an FP16 capture surface into an image, cropped to the given size.</summary>
    /// <param name="keepAlpha">
    /// Keeps the surface's premultiplied alpha, converted to straight alpha; otherwise the image is
    /// opaque, as a display's composition is.
    /// </param>
    public HdrImage Read(IDirect3DSurface surface, int width, int height, bool keepAlpha = false)
    {
        nint surfacePointer = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        nint access = 0, texture = 0, staging = 0;
        try
        {
            access = Vtbl.QueryInterface(surfacePointer, DxgiInterfaceAccess);
            Guid textureIid = Texture2D;
            Native.ThrowIfFailed(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vtbl.Slot(access, 3))(access, &textureIid, &texture),
                "IDirect3DDxgiInterfaceAccess.GetInterface");

            Texture2DDesc desc;
            ((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void>)Vtbl.Slot(texture, 10))(texture, &desc);
            if (desc.Format != FormatRgba16Float)
                throw new InvalidDataException($"Unexpected capture format {desc.Format}.");
            width = Math.Min(width, (int)desc.Width);
            height = Math.Min(height, (int)desc.Height);

            var stagingDesc = new Texture2DDesc
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
                SampleCount = 1,
                Usage = StagingUsage,
                CpuAccessFlags = CpuRead,
            };

            lock (gate)
            {
                ObjectDisposedException.ThrowIf(device == 0, this);
                Native.ThrowIfFailed(((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void*, nint*, int>)Vtbl.Slot(device, 5))(device, &stagingDesc, null, &staging),
                    "ID3D11Device.CreateTexture2D");
                ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Vtbl.Slot(context, 47))(context, staging, texture);
                MappedSubresource mapped;
                Native.ThrowIfFailed(((delegate* unmanaged[Stdcall]<nint, nint, uint, int, uint, MappedSubresource*, int>)Vtbl.Slot(context, 14))(context, staging, 0, MapRead, 0, &mapped),
                    "ID3D11DeviceContext.Map");
                try
                {
                    using var image = new HdrImage(width, height);
                    nint source = (nint)mapped.Data;
                    uint pitch = mapped.RowPitch;
                    ParallelFor(height, y =>
                    {
                        var row = new ReadOnlySpan<Half>((byte*)source + (long)y * pitch, width * 4);
                        Span<Half> target = image.Row(y);
                        row.CopyTo(target);
                        if (keepAlpha)
                            Unpremultiply(target);
                        else
                        {
                            for (int x = 3; x < target.Length; x += 4)
                                target[x] = Half.One;
                        }
                    });
                    return image.Share();
                }
                finally
                {
                    ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Vtbl.Slot(context, 15))(context, staging, 0);
                }
            }
        }
        finally
        {
            Vtbl.Release(staging);
            Vtbl.Release(texture);
            Vtbl.Release(access);
            Marshal.Release(surfacePointer);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            Vtbl.Release(context);
            Vtbl.Release(device);
            context = 0;
            device = 0;
        }

        (WinRTDevice as IDisposable)?.Dispose();
    }

    private static void Unpremultiply(Span<Half> row)
    {
        for (int i = 0; i < row.Length; i += 4)
        {
            float alpha = Math.Clamp((float)row[i + 3], 0, 1);
            if (alpha >= 1)
            {
                row[i + 3] = Half.One;
                continue;
            }

            float inverse = alpha > 0 ? 1 / alpha : 0;
            row[i] = (Half)((float)row[i] * inverse);
            row[i + 1] = (Half)((float)row[i + 1] * inverse);
            row[i + 2] = (Half)((float)row[i + 2] * inverse);
            row[i + 3] = (Half)alpha;
        }
    }

    private static void ParallelFor(int count, Action<int> body)
    {
        if (count < 256)
        {
            for (int i = 0; i < count; i++)
                body(i);
            return;
        }

        Parallel.For(0, count, body);
    }
}

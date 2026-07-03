using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Heliosoph.DatumV.Model.Spatial;

using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Spatial;

/// <summary>
/// CPU point-splat rasterizer shared by <c>pc_render</c> and
/// <c>pc_fuse_render_agg</c>. Projects world-space PointCloud blobs through a
/// pinhole camera and z-buffers square splats into an RGBA bitmap.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Camera model.</strong> Exact inverse of the pinhole unprojection
/// used by <c>point_cloud_from_depth_pinhole</c>: focal length is derived
/// from the <em>vertical</em> FOV (<c>focalPx = (height/2) / tan(fov/2)</c>),
/// the principal point sits at the image center, and pixel centers are at
/// half-integer offsets. A cloud unprojected with a given fov and re-rendered
/// with the same fov, size, and identity pose reproduces the source pixels.
/// </para>
/// <para>
/// <strong>Pose convention.</strong> <c>cameraToWorld</c> is the
/// same camera-to-world 4×4 row-major matrix the reconstruction pipeline
/// accumulates (<c>pose_from_rgbd</c> → <c>pose_compose</c>): applying it to
/// the camera-local origin yields the camera's world position. Rendering
/// inverts it with the rigid-inverse formula (<c>M⁻¹ = [Rᵀ | −Rᵀ·t]</c>),
/// matching <c>pose_inverse</c>.
/// </para>
/// <para>
/// <strong>Coordinate frames.</strong> World space follows the
/// <see cref="PointCloudCoordinateFrame.CameraOpenGl"/> convention (+x right,
/// +y up, −z forward). Points tagged <see cref="PointCloudCoordinateFrame.CameraOpenCv"/>
/// are converted on the fly (negate y and z); Unspecified is treated as GL.
/// </para>
/// <para>
/// <strong>Splats.</strong> Each point paints a <c>pointSizePx</c>-square
/// block centered on its projected pixel, depth-tested per covered pixel
/// against the camera-forward distance. Colorless clouds paint white.
/// Background is opaque black.
/// </para>
/// </remarks>
internal static class PointCloudRasterizer
{
    /// <summary>Minimum camera-forward distance; points at or behind the camera plane are culled.</summary>
    private const float NearClip = 1e-6f;

    /// <summary>
    /// Renders <paramref name="blobs"/> (world-space PointCloud blobs, header +
    /// interleaved payload) from the camera described by
    /// <paramref name="cameraToWorld"/> into a fresh
    /// <see cref="SKColorType.Rgba8888"/> bitmap. Caller owns the bitmap.
    /// </summary>
    public static SKBitmap Render(
        IReadOnlyList<byte[]> blobs,
        ReadOnlySpan<float> cameraToWorld,
        int width,
        int height,
        float fovDeg,
        int pointSizePx)
    {
        // World-to-camera via rigid inverse — same math as pose_inverse.
        float r00 = cameraToWorld[0], r01 = cameraToWorld[1], r02 = cameraToWorld[2],  tx = cameraToWorld[3];
        float r10 = cameraToWorld[4], r11 = cameraToWorld[5], r12 = cameraToWorld[6],  ty = cameraToWorld[7];
        float r20 = cameraToWorld[8], r21 = cameraToWorld[9], r22 = cameraToWorld[10], tz = cameraToWorld[11];

        float w00 = r00, w01 = r10, w02 = r20, wtx = -(r00 * tx + r10 * ty + r20 * tz);
        float w10 = r01, w11 = r11, w12 = r21, wty = -(r01 * tx + r11 * ty + r21 * tz);
        float w20 = r02, w21 = r12, w22 = r22, wtz = -(r02 * tx + r12 * ty + r22 * tz);

        float fovRad = fovDeg * MathF.PI / 180f;
        float focalPx = (height / 2f) / MathF.Tan(fovRad / 2f);
        float cx = width / 2f;
        float cy = height / 2f;

        int pixelCount = width * height;
        float[] zbuf = new float[pixelCount];
        Array.Fill(zbuf, float.PositiveInfinity);
        byte[] rgba = new byte[pixelCount * 4];
        for (int i = 3; i < rgba.Length; i += 4)
        {
            rgba[i] = 255; // opaque black background
        }

        // Splat block offsets: a size-s square centered on the projected pixel
        // (bias toward +x/+y for even sizes, matching integer-center rounding).
        int splatLo = -((pointSizePx - 1) / 2);
        int splatHi = pointSizePx / 2;

        foreach (byte[] blob in blobs)
        {
            PointCloudHeader header = PointCloudHeader.Read(blob);
            int pointCount = checked((int)header.PointCount);
            if (pointCount == 0) continue;

            int stride = header.PointStrideBytes;
            bool hasColor = header.HasColor;
            bool isOpenCv = header.CoordinateFrame == PointCloudCoordinateFrame.CameraOpenCv;
            ReadOnlySpan<byte> src = blob;

            for (int i = 0; i < pointCount; i++)
            {
                int slot = PointCloudHeader.SizeBytes + i * stride;
                float x = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(slot + 0, 4));
                float y = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(slot + 4, 4));
                float z = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(slot + 8, 4));
                if (isOpenCv)
                {
                    y = -y;
                    z = -z;
                }

                float xc = w00 * x + w01 * y + w02 * z + wtx;
                float yc = w10 * x + w11 * y + w12 * z + wty;
                float zc = w20 * x + w21 * y + w22 * z + wtz;

                float forward = -zc; // GL camera looks down −z
                if (!(forward > NearClip) || float.IsNaN(forward)) continue;

                // Inverse of xCv = (u + 0.5 − cx) · forward / focalPx with
                // yCv = −y_gl: solve for the pixel whose center the point hits.
                float uF = cx + xc * focalPx / forward - 0.5f;
                float vF = cy - yc * focalPx / forward - 0.5f;
                if (float.IsNaN(uF) || float.IsNaN(vF)) continue;
                int pu = (int)MathF.Round(uF);
                int pv = (int)MathF.Round(vF);
                if (pu + splatHi < 0 || pu + splatLo >= width) continue;
                if (pv + splatHi < 0 || pv + splatLo >= height) continue;

                byte cr = 255, cg = 255, cb = 255, ca = 255;
                if (hasColor)
                {
                    cr = src[slot + 12];
                    cg = src[slot + 13];
                    cb = src[slot + 14];
                    ca = src[slot + 15];
                }

                for (int dy = splatLo; dy <= splatHi; dy++)
                {
                    int py = pv + dy;
                    if ((uint)py >= (uint)height) continue;
                    for (int dx = splatLo; dx <= splatHi; dx++)
                    {
                        int px = pu + dx;
                        if ((uint)px >= (uint)width) continue;
                        int idx = py * width + px;
                        if (forward < zbuf[idx])
                        {
                            zbuf[idx] = forward;
                            int b = idx * 4;
                            rgba[b + 0] = cr;
                            rgba[b + 1] = cg;
                            rgba[b + 2] = cb;
                            rgba[b + 3] = ca;
                        }
                    }
                }
            }
        }

        SKImageInfo info = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        SKBitmap bitmap = new(info);
        Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
        return bitmap;
    }
}

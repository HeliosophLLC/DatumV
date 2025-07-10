namespace DatumQuery.Functions.Image;

using SkiaSharp;

/// <summary>
/// Wraps an image as either encoded bytes, a decoded <see cref="SKBitmap"/>, or both.
/// Transform functions pass <see cref="ImageHandle"/> through the pipeline so that
/// only the first function in a nested chain decodes and only the final consumer encodes.
/// Implements <see cref="IDisposable"/> to release the native <see cref="SKBitmap"/> memory.
/// </summary>
public sealed class ImageHandle : IDisposable
{
    private byte[]? _encodedBytes;
    private SKBitmap? _bitmap;
    private bool _ownsBitmap;
    private bool _disposed;

    /// <summary>
    /// The image format to use when encoding. Set by the most recent function
    /// in the chain that specified a format (explicit arg or detected from source bytes).
    /// </summary>
    public SKEncodedImageFormat Format { get; }

    /// <summary>
    /// Creates a handle from encoded image bytes. The bitmap is decoded lazily
    /// on the first call to <see cref="GetBitmap"/>.
    /// </summary>
    /// <param name="encodedBytes">The encoded image bytes (JPEG, PNG, or WebP).</param>
    /// <param name="format">The encoding format to use for output.</param>
    public ImageHandle(byte[] encodedBytes, SKEncodedImageFormat format)
    {
        _encodedBytes = encodedBytes;
        Format = format;
    }

    /// <summary>
    /// Creates a handle from a decoded bitmap. The encoded bytes are produced lazily
    /// on the first call to <see cref="GetEncodedBytes"/>.
    /// The handle takes ownership of the bitmap and will dispose it.
    /// </summary>
    /// <param name="bitmap">The decoded bitmap. Ownership transfers to this handle.</param>
    /// <param name="format">The encoding format to use for output.</param>
    public ImageHandle(SKBitmap bitmap, SKEncodedImageFormat format)
    {
        _bitmap = bitmap;
        _ownsBitmap = true;
        Format = format;
    }

    /// <summary>
    /// Returns <c>true</c> if this handle already holds a decoded bitmap,
    /// meaning <see cref="GetBitmap"/> will not trigger a decode.
    /// </summary>
    internal bool HasBitmap => _bitmap is not null;

    /// <summary>
    /// Returns the decoded bitmap, decoding from bytes on first access.
    /// The returned bitmap is owned by this handle — callers must not dispose it.
    /// </summary>
    /// <param name="functionName">The calling function name, used in error messages.</param>
    /// <returns>The decoded <see cref="SKBitmap"/>.</returns>
    /// <exception cref="InvalidOperationException">The image bytes could not be decoded.</exception>
    public SKBitmap GetBitmap(string functionName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_bitmap is not null)
        {
            return _bitmap;
        }

        _bitmap = SKBitmap.Decode(_encodedBytes)
            ?? throw new InvalidOperationException(
                $"{functionName}() failed to decode the image data.");

        _ownsBitmap = true;
        return _bitmap;
    }

    /// <summary>
    /// Returns the encoded bytes, encoding from the bitmap on first access.
    /// </summary>
    /// <returns>The encoded image bytes in the current <see cref="Format"/>.</returns>
    public byte[] GetEncodedBytes()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_encodedBytes is not null)
        {
            return _encodedBytes;
        }

        _encodedBytes = ImageEncoder.Encode(_bitmap!, Format);
        return _encodedBytes;
    }

    /// <summary>
    /// Releases the native <see cref="SKBitmap"/> memory if this handle owns a bitmap.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_ownsBitmap && _bitmap is not null)
        {
            _bitmap.Dispose();
            _bitmap = null;
        }
    }
}

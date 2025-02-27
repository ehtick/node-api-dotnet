// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.JavaScript.NodeApi.Runtime;

using System;
#if !(NETFRAMEWORK || NETSTANDARD)
using System.Buffers;
#endif
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

internal struct Utf8StringArray : IDisposable
{
    // Use one contiguous buffer for all UTF-8 strings.
    private readonly byte[] _stringBuffer;
    private GCHandle _pinnedStringBuffer;

    public unsafe Utf8StringArray(ReadOnlySpan<string> strings)
    {
        int byteLength = 0;
        for (int i = 0; i < strings.Length; i++)
        {
            byteLength += Encoding.UTF8.GetByteCount(strings[i]) + 1;
        }

#if NETFRAMEWORK || NETSTANDARD
        // Avoid a dependency on System.Buffers with .NET Framework.
        // It is available as a Nuget package, but might not be installed in the application.
        // In this case the buffer is not actually pooled.

        Utf8Strings = new nint[strings.Length];
        _stringBuffer = new byte[byteLength];
#else
        Utf8Strings = ArrayPool<nint>.Shared.Rent(strings.Length);
        _stringBuffer = ArrayPool<byte>.Shared.Rent(byteLength);
#endif

        // Pin the string buffer
        _pinnedStringBuffer = GCHandle.Alloc(_stringBuffer, GCHandleType.Pinned);
        nint stringBufferPtr = _pinnedStringBuffer.AddrOfPinnedObject();
        int offset = 0;
        for (int i = 0; i < strings.Length; i++)
        {
            fixed (char* src = strings[i])
            {
                Utf8Strings[i] = stringBufferPtr + offset;
                offset += Encoding.UTF8.GetBytes(
                    src, strings[i].Length, (byte*)(stringBufferPtr + offset), byteLength - offset)
                    + 1; // +1 for the string Null-terminator.
            }
        }
    }

    public void Dispose()
    {
        if (!Disposed)
        {
            Disposed = true;
            _pinnedStringBuffer.Free();

#if !(NETFRAMEWORK || NETSTANDARD)
            ArrayPool<nint>.Shared.Return(Utf8Strings);
            ArrayPool<byte>.Shared.Return(_stringBuffer);
#endif
        }
    }

    public readonly nint[] Utf8Strings { get; }

    public bool Disposed { get; private set; }

    // To support Utf8StringArray usage within a fixed statement.
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly ref nint GetPinnableReference()
    {
        if (Disposed) throw new ObjectDisposedException(nameof(Utf8StringArray));
        Span<nint> span = Utf8Strings;
        return ref span.GetPinnableReference();
    }

    public static unsafe string[] ToStringArray(nint utf8StringArray, int size)
    {
        var utf8Strings = new ReadOnlySpan<nint>((void*)utf8StringArray, size);
        string[] strings = new string[size];
        for (int i = 0; i < utf8Strings.Length; i++)
        {
            strings[i] = PtrToStringUTF8((byte*)utf8Strings[i]);
        }
        return strings;
    }

    public static unsafe string PtrToStringUTF8(byte* ptr)
    {
#if NETFRAMEWORK || NETSTANDARD
        if (ptr == null) throw new ArgumentNullException(nameof(ptr));
        int length = 0;
        while (ptr[length] != 0) length++;
        return Encoding.UTF8.GetString(ptr, length);
#else
        return Marshal.PtrToStringUTF8((nint)ptr) ?? throw new ArgumentNullException(nameof(ptr));
#endif
    }
}

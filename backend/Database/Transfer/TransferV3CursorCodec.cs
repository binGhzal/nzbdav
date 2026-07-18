using System.Buffers.Binary;
using System.Text;

namespace NzbWebDAV.Database.Transfer;

internal enum TransferV3CursorComponentType : byte
{
    Uuid = 0x01,
    SignedInteger = 0x02,
    Text = 0x03,
}

internal readonly record struct TransferV3CursorComponent
{
    private TransferV3CursorComponent(
        TransferV3CursorComponentType type,
        Guid uuidValue,
        long integerValue,
        string? textValue)
    {
        Type = type;
        UuidValue = uuidValue;
        IntegerValue = integerValue;
        TextValue = textValue;
    }

    internal TransferV3CursorComponentType Type { get; }

    internal Guid UuidValue { get; }

    internal long IntegerValue { get; }

    internal string? TextValue { get; }

    internal static TransferV3CursorComponent FromGuid(Guid value) =>
        new(TransferV3CursorComponentType.Uuid, value, default, null);

    internal static TransferV3CursorComponent FromInt64(long value) =>
        new(TransferV3CursorComponentType.SignedInteger, default, value, null);

    internal static TransferV3CursorComponent FromText(string value) =>
        new(
            TransferV3CursorComponentType.Text,
            default,
            default,
            value ?? throw new ArgumentNullException(nameof(value)));
}

internal static class TransferV3CursorCodec
{
    internal const byte FormatVersion = 0x01;
    internal const int MaxComponentCount = 32;
    internal const int MaxTextBytes = 64 * 1024;
    internal const int MaxCursorBytes = 256 * 1024;
    internal const int MaxEncodedChars = ((MaxCursorBytes + 2) / 3) * 4;

    private const int ComponentHeaderLength = 5;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Encode(params TransferV3CursorComponent[] components)
    {
        ArgumentNullException.ThrowIfNull(components);
        if (components.Length is 0 or > MaxComponentCount)
        {
            throw new ArgumentException(
                $"A cursor must contain between 1 and {MaxComponentCount} components.",
                nameof(components));
        }

        var payloadLengths = new int[components.Length];
        var totalLength = 2;
        for (var index = 0; index < components.Length; index++)
        {
            var payloadLength = GetPayloadLength(components[index], nameof(components));
            payloadLengths[index] = payloadLength;
            totalLength = checked(totalLength + ComponentHeaderLength + payloadLength);
            if (totalLength > MaxCursorBytes)
            {
                throw new ArgumentException(
                    $"The encoded cursor exceeds the {MaxCursorBytes}-byte limit.",
                    nameof(components));
            }
        }

        var bytes = new byte[totalLength];
        bytes[0] = FormatVersion;
        bytes[1] = checked((byte)components.Length);
        var offset = 2;
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            var payloadLength = payloadLengths[index];
            bytes[offset++] = (byte)component.Type;
            BinaryPrimitives.WriteUInt32BigEndian(
                bytes.AsSpan(offset, sizeof(uint)),
                checked((uint)payloadLength));
            offset += sizeof(uint);
            WritePayload(component, bytes.AsSpan(offset, payloadLength));
            offset += payloadLength;
        }

        return EncodeBase64Url(bytes);
    }

    internal static IReadOnlyList<TransferV3CursorComponent> Decode(string encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (encoded.Length is 0 || encoded.Length > MaxEncodedChars)
        {
            throw InvalidCursor();
        }

        var bytes = DecodeBase64Url(encoded);
        if (bytes.Length < 2 || bytes.Length > MaxCursorBytes || bytes[0] != FormatVersion)
        {
            throw InvalidCursor();
        }

        var componentCount = bytes[1];
        if (componentCount is 0 or > MaxComponentCount)
        {
            throw InvalidCursor();
        }

        var components = new TransferV3CursorComponent[componentCount];
        var offset = 2;
        for (var index = 0; index < componentCount; index++)
        {
            if (bytes.Length - offset < ComponentHeaderLength)
            {
                throw InvalidCursor();
            }

            var type = (TransferV3CursorComponentType)bytes[offset++];
            var declaredLength = BinaryPrimitives.ReadUInt32BigEndian(
                bytes.AsSpan(offset, sizeof(uint)));
            offset += sizeof(uint);

            ValidateDeclaredLength(type, declaredLength);
            if (declaredLength > bytes.Length - offset)
            {
                throw InvalidCursor();
            }

            var payloadLength = checked((int)declaredLength);
            var payload = bytes.AsSpan(offset, payloadLength);
            components[index] = ReadComponent(type, payload);
            offset += payloadLength;
        }

        if (offset != bytes.Length)
        {
            throw InvalidCursor();
        }

        return components;
    }

    internal static int Compare(string left, string right)
    {
        var leftComponents = Decode(left);
        var rightComponents = Decode(right);
        if (leftComponents.Count != rightComponents.Count)
        {
            throw new FormatException("Cursor component shapes do not match.");
        }

        for (var index = 0; index < leftComponents.Count; index++)
        {
            var leftComponent = leftComponents[index];
            var rightComponent = rightComponents[index];
            if (leftComponent.Type != rightComponent.Type)
            {
                throw new FormatException("Cursor component shapes do not match.");
            }

            var comparison = CompareComponent(leftComponent, rightComponent);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    internal static string EncodeBase64Url(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    internal static byte[] DecodeBase64Url(string encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (encoded.Length == 0)
        {
            return [];
        }

        if (encoded.Length % 4 == 1)
        {
            throw InvalidCursor();
        }

        foreach (var character in encoded)
        {
            if (!IsBase64UrlCharacter(character))
            {
                throw InvalidCursor();
            }
        }

        var paddingLength = (4 - encoded.Length % 4) % 4;
        var standard = encoded.Replace('-', '+').Replace('_', '/')
            + new string('=', paddingLength);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(standard);
        }
        catch (FormatException)
        {
            throw InvalidCursor();
        }

        if (!string.Equals(EncodeBase64Url(bytes), encoded, StringComparison.Ordinal))
        {
            throw InvalidCursor();
        }

        return bytes;
    }

    private static int GetPayloadLength(
        TransferV3CursorComponent component,
        string parameterName)
    {
        return component.Type switch
        {
            TransferV3CursorComponentType.Uuid => 16,
            TransferV3CursorComponentType.SignedInteger => sizeof(long),
            TransferV3CursorComponentType.Text => GetTextPayloadLength(
                component.TextValue,
                parameterName),
            _ => throw new ArgumentException("The cursor component type is invalid.", parameterName),
        };
    }

    private static int GetTextPayloadLength(string? value, string parameterName)
    {
        if (value is null || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Cursor text must be non-null and contain no NUL.", parameterName);
        }

        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Cursor text is not valid Unicode.", parameterName, exception);
        }

        if (byteCount > MaxTextBytes)
        {
            throw new ArgumentException(
                $"Cursor text exceeds the {MaxTextBytes}-byte limit.",
                parameterName);
        }

        return byteCount;
    }

    private static void WritePayload(
        TransferV3CursorComponent component,
        Span<byte> destination)
    {
        switch (component.Type)
        {
            case TransferV3CursorComponentType.Uuid:
                if (!component.UuidValue.TryWriteBytes(
                        destination,
                        bigEndian: true,
                        out var bytesWritten)
                    || bytesWritten != 16)
                {
                    throw new InvalidOperationException("Could not encode the UUID cursor component.");
                }

                break;
            case TransferV3CursorComponentType.SignedInteger:
                var sortable = unchecked((ulong)(component.IntegerValue ^ long.MinValue));
                BinaryPrimitives.WriteUInt64BigEndian(destination, sortable);
                break;
            case TransferV3CursorComponentType.Text:
                StrictUtf8.GetBytes(component.TextValue!, destination);
                break;
            default:
                throw new InvalidOperationException("The cursor component type is invalid.");
        }
    }

    private static void ValidateDeclaredLength(
        TransferV3CursorComponentType type,
        uint declaredLength)
    {
        var valid = type switch
        {
            TransferV3CursorComponentType.Uuid => declaredLength == 16,
            TransferV3CursorComponentType.SignedInteger => declaredLength == sizeof(long),
            TransferV3CursorComponentType.Text => declaredLength <= MaxTextBytes,
            _ => false,
        };
        if (!valid)
        {
            throw InvalidCursor();
        }
    }

    private static TransferV3CursorComponent ReadComponent(
        TransferV3CursorComponentType type,
        ReadOnlySpan<byte> payload)
    {
        switch (type)
        {
            case TransferV3CursorComponentType.Uuid:
                return TransferV3CursorComponent.FromGuid(new Guid(payload, bigEndian: true));
            case TransferV3CursorComponentType.SignedInteger:
                var sortable = BinaryPrimitives.ReadUInt64BigEndian(payload);
                return TransferV3CursorComponent.FromInt64(
                    unchecked((long)(sortable ^ 0x8000000000000000UL)));
            case TransferV3CursorComponentType.Text:
                string text;
                try
                {
                    text = StrictUtf8.GetString(payload);
                }
                catch (DecoderFallbackException)
                {
                    throw InvalidCursor();
                }

                if (text.Contains('\0', StringComparison.Ordinal))
                {
                    throw InvalidCursor();
                }

                return TransferV3CursorComponent.FromText(text);
            default:
                throw InvalidCursor();
        }
    }

    private static int CompareComponent(
        TransferV3CursorComponent left,
        TransferV3CursorComponent right)
    {
        return left.Type switch
        {
            TransferV3CursorComponentType.Uuid => CompareUuid(left.UuidValue, right.UuidValue),
            TransferV3CursorComponentType.SignedInteger =>
                left.IntegerValue.CompareTo(right.IntegerValue),
            TransferV3CursorComponentType.Text => CompareBytes(
                StrictUtf8.GetBytes(left.TextValue!),
                StrictUtf8.GetBytes(right.TextValue!)),
            _ => throw InvalidCursor(),
        };
    }

    private static int CompareUuid(Guid left, Guid right)
    {
        Span<byte> leftBytes = stackalloc byte[16];
        Span<byte> rightBytes = stackalloc byte[16];
        left.TryWriteBytes(leftBytes, bigEndian: true, out _);
        right.TryWriteBytes(rightBytes, bigEndian: true, out _);
        return CompareBytes(leftBytes, rightBytes);
    }

    private static int CompareBytes(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var commonLength = Math.Min(left.Length, right.Length);
        for (var index = 0; index < commonLength; index++)
        {
            var comparison = left[index].CompareTo(right[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static bool IsBase64UrlCharacter(char character) =>
        character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-'
            or '_';

    private static FormatException InvalidCursor() =>
        new("The transfer cursor is malformed or noncanonical.");
}

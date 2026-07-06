using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Models;
using NzbWebDAV.Extensions;

namespace backend.Tests.Extensions;

public sealed class RarHeaderExtensionsTests
{
    [Fact]
    public void Rar3AesKeyDerivationAvoidsLargeTemporaryBuffers()
    {
        var salt = Enumerable.Range(0, 8).Select(x => (byte)x).ToArray();
        var method = typeof(RarHeaderExtensions).GetMethod(
            "GetRar3AesParams",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        _ = InvokeRar3AesParams(method, salt, "password", 1024);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = InvokeRar3AesParams(method, salt, "password", 2048);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var reference = GetRar3AesParamsReference(salt, "password", 2048);

        Assert.Equal(16, result.Iv.Length);
        Assert.Equal(16, result.Key.Length);
        Assert.Equal(2048, result.DecodedSize);
        Assert.Equal(reference.Iv, result.Iv);
        Assert.Equal(reference.Key, result.Key);
        Assert.True(allocated < 1_000_000, $"Expected less than 1MB allocated, got {allocated:N0} bytes.");
    }

    private static AesParams InvokeRar3AesParams(MethodInfo method, byte[] salt, string password, long decodedSize)
    {
        return Assert.IsType<AesParams>(method.Invoke(null, [salt, password, decodedSize]));
    }

    private static AesParams GetRar3AesParamsReference(byte[] salt, string password, long decodedSize)
    {
        const int sizeInitV = 0x10;
        const int sizeSalt30 = 0x08;
        var aesIV = new byte[sizeInitV];

        var rawLength = 2 * password.Length;
        var rawPassword = new byte[rawLength + sizeSalt30];
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        for (var i = 0; i < password.Length; i++)
        {
            rawPassword[i * 2] = passwordBytes[i];
            rawPassword[(i * 2) + 1] = 0;
        }

        for (var i = 0; i < salt.Length; i++)
        {
            rawPassword[i + rawLength] = salt[i];
        }

        using var msgDigest = SHA1.Create();
        const int noOfRounds = 1 << 18;
        const int iblock = 3;
        byte[] digest;
        var data = new byte[(rawPassword.Length + iblock) * noOfRounds];

        for (var i = 0; i < noOfRounds; i++)
        {
            rawPassword.CopyTo(data, i * (rawPassword.Length + iblock));

            data[(i * (rawPassword.Length + iblock)) + rawPassword.Length + 0] = (byte)i;
            data[(i * (rawPassword.Length + iblock)) + rawPassword.Length + 1] = (byte)(i >> 8);
            data[(i * (rawPassword.Length + iblock)) + rawPassword.Length + 2] = (byte)(i >> 16);

            if (i % (noOfRounds / sizeInitV) == 0)
            {
                digest = msgDigest.ComputeHash(data, 0, (i + 1) * (rawPassword.Length + iblock));
                aesIV[i / (noOfRounds / sizeInitV)] = digest[19];
            }
        }

        digest = msgDigest.ComputeHash(data);
        var aesKey = CreateRar3AesKey(digest);
        return new AesParams { Iv = aesIV, Key = aesKey, DecodedSize = decodedSize };
    }

    private static byte[] CreateRar3AesKey(byte[] digest)
    {
        var aesKey = new byte[0x10];
        for (var i = 0; i < 4; i++)
        {
            for (var j = 0; j < 4; j++)
            {
                aesKey[(i * 4) + j] = (byte)(
                    (
                        ((digest[i * 4] * 0x1000000) & 0xff000000)
                        | (uint)((digest[(i * 4) + 1] * 0x10000) & 0xff0000)
                        | (uint)((digest[(i * 4) + 2] * 0x100) & 0xff00)
                        | (uint)(digest[(i * 4) + 3] & 0xff)
                    ) >> (j * 8)
                );
            }
        }

        return aesKey;
    }
}

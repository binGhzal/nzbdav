using NzbWebDAV.Extensions;

namespace backend.Tests.Extensions;

public sealed class ObjectExtensionsTests
{
    [Fact]
    public void GetReflectionPropertyReadsPrivateProperties()
    {
        var item = new ReflectionTarget();

        Assert.Equal("value", item.GetReflectionProperty("PrivateProperty"));
    }

    [Fact]
    public void GetReflectionFieldReadsPrivateFields()
    {
        var item = new ReflectionTarget();

        Assert.Equal(42, item.GetReflectionField("_privateField"));
    }

    [Fact]
    public void MissingReflectionMembersReturnNull()
    {
        var item = new ReflectionTarget();

        Assert.Null(item.GetReflectionProperty("MissingProperty"));
        Assert.Null(item.GetReflectionField("MissingField"));
    }

    private sealed class ReflectionTarget
    {
        private readonly int _privateField = 42;
        private int PrivateFieldValue => _privateField;
        private string PrivateProperty { get; } = "value";
    }
}

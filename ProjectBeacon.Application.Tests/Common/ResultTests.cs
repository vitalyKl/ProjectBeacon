namespace ProjectBeacon.Application.Tests;

using ProjectBeacon.Application.Common;

public sealed class ResultTests
{
    [Fact]
    public void Ok_SetsSuccessAndValue()
    {
        var result = Result.Ok(42);

        Assert.True(result.Success);
        Assert.Equal(42, result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Ok_String_SetsCorrectValue()
    {
        var result = Result.Ok("hello");

        Assert.True(result.Success);
        Assert.Equal("hello", result.Value);
    }

    [Fact]
    public void Failure_SetsErrorAndNoSuccess()
    {
        var result = Result.Failure<string>("something went wrong");

        Assert.False(result.Success);
        Assert.Null(result.Value);
        Assert.Equal("something went wrong", result.Error);
    }

    [Fact]
    public void Failure_WithInt_SetsDefaultAndError()
    {
        var result = Result.Failure<int>("bad value");

        Assert.False(result.Success);
        Assert.Equal(default, result.Value);
        Assert.Equal("bad value", result.Error);
    }
}

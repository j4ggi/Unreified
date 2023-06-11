namespace Unreified.Tests;

public class StepIOTests
{
    [Theory]
    [InlineData(42)]
    [InlineData(StringComparison.Ordinal)]
    public void Are_equal_for_the_same_exact_value_dependency(object value)
    {
        Assert.True(new Step.StepIO(value) == new Step.StepIO(value));
        Assert.Equal(new Step.StepIO(value), new Step.StepIO(value));
    }

    [Fact]
    public void Are_equal_for_the_same_named_dependency()
    {
        Assert.True(new Step.StepIO("named") == new Step.StepIO("named"));
        Assert.Equal(new Step.StepIO("named"), new Step.StepIO("named"));
    }

    [Fact]
    public void Are_equal_for_the_same_typed_dependency()
    {
        Assert.True(new Step.StepIO(typeof(int)) == new Step.StepIO(typeof(int)));
        Assert.Equal(new Step.StepIO(typeof(int)), new Step.StepIO(typeof(int)));
    }

#nullable disable
    [Fact]
    public void Throws_on_null_typed_dependency()
    {
        Assert.ThrowsAny<ArgumentNullException>(() => new Step.StepIO(null as Type));
    }

    [Fact]
    public void Throws_on_null_named_dependency()
    {
        Assert.ThrowsAny<ArgumentNullException>(() => new Step.StepIO(null as string));
    }

    [Fact]
    public void Throws_on_null_exact_value_dependency()
    {
        Assert.ThrowsAny<ArgumentNullException>(() => new Step.StepIO(null as object));
    }
#nullable restore
}

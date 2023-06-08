namespace Unreified;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public class InputAttribute : Attribute
{
    public InputAttribute(Type input)
    {
        Input = new(input);
    }

    public InputAttribute(string input)
    {
        Input = new(input);
    }

    public InputAttribute(object input)
    {
        Input = new(input);
    }

    public Step.StepIO Input { get; }
}

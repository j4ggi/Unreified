namespace Unreified;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public class OutputAttribute : Attribute
{
    public OutputAttribute(Type output)
    {
        Output = new(output);
    }

    public OutputAttribute(string output)
    {
        Output = new(output);
    }

    public OutputAttribute(object output)
    {
        Output = new(output);
    }

    public Step.StepIO Output { get; }
}

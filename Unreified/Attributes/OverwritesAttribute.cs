namespace Unreified;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public class OverwritesAttribute : Attribute
{
    public OverwritesAttribute(Type ouput)
    {
        Output = new(ouput);
    }

    public OverwritesAttribute(string ouput)
    {
        Output = new(ouput);
    }

    public OverwritesAttribute(object ouput)
    {
        Output = new(ouput);
    }

    public Step.StepIO Output { get; }
}
